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

When choosing between them:
- If only one client performs the action, sync the action.
- If all clients already execute the same simulation method, do **not** sync it again; only patch RNG or local-only behavior if that method is still nondeterministic.
- If a local UI only ends by calling an action vanilla MP already syncs (for example `BillStack.AddBill` or normal `Bill_Production` config), keep the UI local and do **not** add extra compat sync unless the mod bypasses vanilla sync paths or adds nondeterminism before the final action.
- Prefer attribute-style patches and the existing patch loader over manual Harmony wiring unless there is a concrete reason not to.

## RNG rules

### Safe RNG
Usually **do not patch** RNG that runs during normal simulation/ticking/mapgen/incident execution.
If all clients execute the same code path in the same order, using RNG is just part of gameplay and is already deterministic.

Examples:
- ticking comps
- incident resolution
- raid composition during simulation
- map generation
- pawn generation paths that already execute as shared simulation

### Unsafe RNG
Patch RNG when it happens in **client-local UI/interface code** or otherwise outside synchronized simulation.

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

## Gizmo and dialog rules

### Good
- keep the original gizmo
- sync the original lambda/delegate that performs the real state change
- keep float menus / confirmation dialogs local
- sync only the final chosen action
- let local previews, staged edits, and temporary window state stay local when the real commit happens later
- if the final commit is already synced by vanilla MP, do not wrap it again in compat just because it came from mod UI

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
5. Sync that original action instead of rebuilding UI.
6. Validate lambda ordinals against source, and if possible against compiled assemblies.

## Maintenance rules

- Prefer upstream method calls over compat-side clones.
- Prefer short compat files with obvious intent.
- If a patch starts needing lots of reflection fields and copied UI logic, stop and look for a smaller original delegate/method to sync.
- Comments should explain **why** a thing is unsafe in MP, not just that it uses RNG.
- If you add a patch, be able to say exactly what desync it prevents.

## Good end state

A good compat patch should look like this:
- a few sync registrations
- maybe one or two small prefix/postfix/transpiler hooks
- original mod UI preserved
- no copied gameplay implementation
- only real desync risks handled
- no extra "just in case" syncing

