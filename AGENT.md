# MP Compat patching guide

## Core principles

- Patch as little as possible.
- Keep UI local unless the UI action changes shared game state.
- Sync only the smallest original action that changes shared state.
- Sync the mod's original methods, lambdas, or delegates whenever possible.
- Do **not** recreate gizmos, dialogs, or gameplay logic unless there is no smaller option.
- Do **not** copy large chunks of mod code into compat patches.
- Do **not** patch extra features just because they are nearby in code. Fix the proven desync surface, not everything the mod can do.

## Preferred patch style

Prefer this order of solutions:

1. `MP.RegisterSyncMethod(...)` for named methods.
2. `MpCompat.RegisterLambdaMethod(...)` or `MpCompat.RegisterLambdaDelegate(...)` for original gizmo/dialog lambdas.
3. `[MpCompatPrefix]`, `[MpCompatPostfix]`, `[MpCompatFinalizer]`, `[MpCompatTranspiler]` for small Harmony hooks.
4. `[MpCompatSyncMethod]`, `[MpCompatSyncWorker]`, `[MpCompatSyncField]` via `MpCompatPatchLoader.LoadPatch<T>()`.
5. Only if absolutely necessary, a tiny wrapper/transpiler that routes an original mod call through a synced method.

Timing and registration defaults:
- Register patches immediately by default.
- Use `LongEventHandler.ExecuteWhenFinished(...)` / `LatePatch` only when the mod's types are not safe to touch early (for example static constructors that load textures/assets, or types that only exist after long-event startup).
- If a gizmo lambda only calls a named instance/static method that already represents the real state change, prefer syncing that named method directly instead of the lambda.
- Mark dev-only actions explicitly with `.SetDebugOnly()` and skip known non-gameplay/debug ordinals with `.Skip(...)` when using grouped lambda registrations.

When choosing between them:
- If only one client performs the action, sync the action.
- If all clients already execute the same simulation method, do **not** sync it again; only patch RNG or local-only behavior if that method is still nondeterministic.
- If a local UI only ends by calling an action vanilla MP already syncs (for example `BillStack.AddBill` or normal `Bill_Production` config), keep the UI local and do **not** add extra compat sync unless the mod bypasses vanilla sync paths or adds nondeterminism before the final action.
- Prefer attribute-style patches and the existing patch loader over manual Harmony wiring unless there is a concrete reason not to.
- Do **not** add custom "failed to resolve runtime types/members" scaffolding in a compat file. Prefer normal accessor setup and let `MpCompatPatchLoader` surface registration failures centrally.
- When reflection is needed, prefer `AccessTools.FieldRefAccess(...)` / `AccessTools.StaticFieldRefAccess(...)` over raw `FieldInfo` whenever feasible; use raw `FieldInfo` only when a field ref is not practical. Likewise, prefer cached invokers like `MethodInvoker.GetHandler(...)` over repeated raw `MethodInfo.Invoke(...)` plumbing.

## RNG rules

### Safe RNG
Usually **do not patch** RNG that runs during normal simulation/ticking/mapgen/incident execution.
If all clients execute the same code path in the same order, using RNG is just part of gameplay and is already deterministic.

`Verse.Rand` is the multiplayer-managed RNG, but it is only safe when the callsite itself is safe (shared deterministic simulation, or an explicitly seeded/isolated entry point). Do **not** assume a `Verse.Rand` call is safe just because it uses `Rand`; check whether it runs in local UI, a client-only preview, or a shared path with proven divergent incoming RNG state.

Examples:
- ticking comps
- incident resolution
- raid composition during simulation
- map generation
- pawn generation paths that already execute as shared simulation

### Unsafe RNG
Patch RNG when it happens in **client-local UI/interface code** or otherwise outside synchronized simulation.

Treat `System.Random` and `UnityEngine.Random` as suspicious by default. If a mod uses them in gameplay logic, usually redirect them through `PatchingUtilities.PatchSystemRand(...)`, `PatchSystemRandCtor(...)`, or `PatchUnityRand(...)` rather than leaving them untouched.

Treat collection shuffling and random selection as RNG-sensitive too. `Shuffle`, `InRandomOrder`, `RandomElement`, and `TryRandomElement` are only as deterministic as both the RNG state **and** the source collection order. If the source is not in a stable order across clients (for example `HashSet`, `Dictionary`, cached lists built from unstable traversal, or UI-generated lists), sort/materialize it to a stable order first or patch the callsite.

Examples:
- dialog drawing code choosing a random result
- local gizmo code that mutates state using RNG before a synced action runs
- client-only preview/selection code consuming `Rand`

### Proven simulation RNG desyncs
Sometimes a method runs on every client but still desyncs because the RNG state entering that method is not guaranteed to match.
Only patch these when there is real evidence (desync log, reproducible issue, or a clearly isolated nondeterministic entry point).

In that case, prefer a **seeded** wrapper:
- `Rand.PushState(seed)` in a prefix
- `Rand.PopState()` in a postfix/finalizer

This is different from syncing the method:
- use a **sync method** when only one client performs the action
- use a **seeded RNG wrapper** when all clients already run the same method, but need the same random result

Use the `PatchingUtilities` helpers as follows:
- `PatchSystemRand(...)` / `PatchSystemRandCtor(...)` for `System.Random`
- `PatchUnityRand(...)` for `UnityEngine.Random`
- `PatchPushPopRand(...)` only to isolate `Verse.Rand` side effects when seeded determinism is not the goal

Do **not** use plain `Rand.PushState()` / `Rand.PopState()` as a magic fix for shared-state RNG.
Unseeded push/pop only isolates RNG side effects; it does not make different clients roll the same result.

For UI RNG, prefer:
- `Rand.PushState()` in a prefix
- `Rand.PopState()` in a postfix/finalizer

Use a finalizer if cleanup must happen even when UI code throws.

### Evidence threshold
Do **not** patch RNG just because a method uses `Rand`.
Patch it only when one of these is true:
- the RNG runs in local-only UI code
- you have a real desync trace pointing at that path
- you can clearly show the method is entered with potentially different RNG state on different clients
- or the random result depends on an input collection whose iteration order is not guaranteed to match across clients

## Gizmo and dialog rules

### Good
- keep the original gizmo
- sync the original lambda/delegate that performs the real state change
- if the lambda only forwards to a named method, sync that named method instead
- keep float menus / confirmation dialogs local
- sync only the final chosen action
- let local previews, staged edits, and temporary window state stay local when the real commit happens later
- if the final commit is already synced by vanilla MP, do not wrap it again in compat just because it came from mod UI
- for dialogs that continuously edit shared fields, prefer watching the specific sync fields during `DoWindowContents` and use shared dialog-close helpers where appropriate instead of syncing the whole window
- if UI code mutates shared state locally before the final synced action, snapshot the old state, revert the local mutation, then apply the confirmed change through sync

### Bad
- rebuilding the entire gizmo yourself
- copying the mod's internal button logic into compat
- reimplementing side effects that already exist in the mod
- opening local-only UI for every client when only the final confirmed action matters

## Settings rules

Treat per-player mod settings carefully.

Important: syncable mod configs are already synchronized between clients on join.
Do **not** add compat patches for settings just to force them to match in multiplayer unless you have evidence that a particular setting is not covered by config sync, is used before sync applies, or still causes nondeterminism after join.

### Harmless settings
These are usually fine to leave alone if they only affect:
- local notifications
- dev logging
- purely visual UI
- client-side previews

### Dangerous settings
These need attention if they affect shared simulation/state, for example:
- incident points or timing
- map generation sizes
- whether an event/ability can happen
- whether shared objects spawn/despawn
- letter/message path differences during simulation

If a setting affects simulation, do one of:
- sync the resulting action/state
- force a deterministic/common behavior in MP
- or patch the upstream mod if possible

Do **not** ignore player settings unless they are proven unsafe for MP and there is no better small fix.
Also do **not** override settings to defaults just because they look scary; if configs already sync on join, leave them alone unless there is a real MP problem left to solve.

## Audit checklist before patching a mod

1. Search the whole mod for:
   - `Rand.`
   - `RandomElement`
   - `RandomInRange`
   - `BeginTargeting`
   - `Find.WindowStack.Add`
   - `Command_Action`
   - `Command_Toggle`
   - `DoWindowContents`
   - mod settings reads
2. Classify each result:
   - shared simulation
   - UI-only
   - dev-only
3. Trace every player-facing action to the method/lambda that actually changes shared state.
4. Decide whether the fix is:
   - syncing a final action, or
   - seeding/isolating RNG inside a method that already runs on all clients.
5. Prefer a named method over a lambda when the lambda is only a thin forwarder.
6. Sync that original action instead of rebuilding UI.
7. Validate lambda ordinals against source, and if possible against compiled assemblies.
8. For any `RandomElement`/`Shuffle`/`InRandomOrder` use, verify that the input collection order is stable before deciding the RNG is safe.

## Maintenance rules

- Prefer upstream method calls over compat-side clones.
- Prefer short compat files with obvious intent.
- If a patch starts needing lots of reflection fields and copied UI logic, stop and look for a smaller original delegate/method to sync.
- If a patch starts inventing its own runtime-resolution/error-reporting framework, stop and fold it back into the existing MP-Compat patterns.
- Default to field refs for field access in compat code. Do not reach for `FieldInfo` first unless the field-ref approach is genuinely not workable for that case.
- Prefer stable ordering before random choice. If random selection is made from a collection built from unordered traversal, sort by a deterministic key (`thingIDNumber`, `loadID`, `defName`, index, etc.) before selection or document why the source order is already stable.
- `SyncWorker`s should serialize stable identifiers (things, defs, indices, map/zone references, etc.) and reconstruct transient commands/dialog objects on the other side instead of trying to sync the whole runtime object graph.
- Avoid long-lived static compat state. If a temporary guard/swap is needed, bracket it tightly and restore it with `try/finally`.
- Comments should explain **why** a thing is unsafe in MP, not just that it uses RNG.
- If you add a patch, be able to say exactly what desync it prevents.

## Full audit requirement

When writing OR updating a compat patch for any mod, **always audit the entire mod source first**. Never assume the existing patch covered everything — original quality is unknown. Walk the whole codebase before touching a single line of compat code.

Mandatory audit steps:
1. Read every file that ends in `.cs` in the mod source.
2. Record every `Command_Action`, `Command_Toggle`, `CompGetGizmosExtra`, `CompGetWornGizmosExtra`, `DoWindowContents`, gizmo lambda, `BeginTargeting`, `Rand.`/`Random`/`RandomElement`/`RandomInRange`/`Shuffle`/`InRandomOrder` call.
3. For each one, trace it to the method that actually changes shared game state.
4. Then and only then, decide what is already covered, what is newly needed, and what is safe to skip.

Do **not** skip this step just because a patch already exists or looks comprehensive.

## Sync worker type resolution — the declared-type rule

MP serialises a synced method's `this` using the **declared** target type from `RegisterSyncMethod`, never the runtime type.

Consequence: if you call `MP.RegisterSyncWorker<T>(handler, ConcreteSubclass)`, that worker will **never** be invoked when the sync method's `this` was registered against a *base* type.  
`SyncWorkerDictionaryTree.TryGetValue` first checks `explicitEntries` keyed by the exact declared type, then walks `implicitEntries` trees.  A per-subclass explicit registration is useless if the sync method was registered on the base class.

Rule: when a sync method is registered on a base type B, the sync worker **must** be registered for **B itself** (as explicit) — not for individual subtypes.

```
// WRONG — worker never called because StartAbilityJob is registered on BaseAbility
MP.RegisterSyncWorker<ITargetingSource>(handler, typeof(ConcreteAbility)); // subtype

// CORRECT — explicit entry keyed on the exact declared type wins over any implicit entry
MP.RegisterSyncWorker<ITargetingSource>(handler, typeof(BaseAbility));     // base type
```

If an upstream compat (e.g., VEF) already registers an *implicit* worker for B, you can safely override it by registering an *explicit* worker for B — explicit entries are checked first.  Your explicit worker should implement the full behaviour for all subtypes (both the upstream case and your mod's special case).

## Good end state

A good compat patch should look like this:
- a few sync registrations
- maybe one or two small prefix/postfix/transpiler hooks
- original mod UI preserved
- no copied gameplay implementation
- only real desync risks handled
- no extra "just in case" syncing

