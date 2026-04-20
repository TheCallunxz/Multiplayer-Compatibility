using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Deep And Deeper by Shashlichnik</summary>
[MpCompatFor("Shashlichnik.DeepAndDeeper")]
internal class DeepAndDeeper
{
    private static readonly MethodInfo findCurrentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));
    private static readonly Type factionExtensionsType = AccessTools.TypeByName("Multiplayer.Client.Factions.FactionExtensions");
    private static readonly MethodInfo pushFactionMethod = AccessTools.Method(factionExtensionsType, "PushFaction", new[] { typeof(Map), typeof(Faction), typeof(bool) });
    private static readonly MethodInfo popFactionMethod = AccessTools.Method(factionExtensionsType, "PopFaction", new[] { typeof(Map) });

    private static bool missingPushFactionWarningLogged;
    private static bool missingPopFactionWarningLogged;

    public DeepAndDeeper(ModContentPack mod)
    {
        // Register gizmo callbacks after loading so CaveEntrance static texture init runs on main thread.
        // Also defer CaveEntrance:Tick rand patch — Harmony JIT-compiling it triggers CaveEntrance's static
        // constructor which loads textures and must happen on the main thread.
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            // Gizmos that change shared state.
            MpCompat.RegisterLambdaMethod("Shashlichnik.CaveEntrance", "GetGizmos", 2);
            MpCompat.RegisterLambdaMethod("Shashlichnik.CaveExit", "GetGizmos", 1);

            // Dev-only cave controls.
            MpCompat.RegisterLambdaMethod("Shashlichnik.CaveEntrance", "GetGizmos", 3, 4).SetDebugOnly();
            MP.RegisterSyncMethod(AccessTools.DeclaredMethod("Shashlichnik.DebugActions:MoveAllPawns")).SetDebugOnly();

            // Deferred: patching CaveEntrance:Tick triggers its static ctor (texture loads) via Harmony JIT.
            PatchingUtilities.PatchPushPopRand("Shashlichnik.CaveEntrance:Tick");
        });

        // Local camera/ambient visual paths call Rand and can desync RNG state.
        // CaveEntrance:Tick is deferred above; ProcessAmbient is on MapComponent so safe to patch eagerly.
        PatchingUtilities.PatchPushPopRand("Shashlichnik.CaveMapComponent:ProcessAmbient");

        // GenStep_DeepDiver.TrySpawnInterestAt uses Find.CurrentMap but GenStep is not a supported type
        // for ReplaceCurrentMapUsage. A custom transpiler replaces it with the already-available `map` arg.
        // (see TrySpawnInterestAtTranspiler below)

        MpCompatPatchLoader.LoadPatch<DeepAndDeeper>();
    }

    // WorkGiver_GoDownIfJobUnderground.IsAlreadyReserved uses Faction.OfPlayer, which returns
    // different factions on different clients in multiplayer. For example, if the host player has
    // colonists who already reserved mine targets in the cave, IsAlreadyReserved returns true on
    // the host but false on a client (whose faction has no reservations there). This causes
    // TryFindFirstAvailableJobTargetAt to return different results per machine — on the client it
    // finds valid mine work and creates a candidate job, on the host it finds nothing and falls
    // through to a wander job. The different job counts desync the UniqueID counter and cascade
    // into a full desync whenever AutoEnter is enabled.
    //
    // Fix: transpile IsTargetAvailable to replace the `this.IsAlreadyReserved(target)` call with
    // a static helper that checks reservations against forPawn.Faction (the mining pawn's own
    // faction) rather than Faction.OfPlayer (the local client's player faction).
    [MpCompatTranspiler("Shashlichnik.WorkGiver_GoDownIfJobUnderground", "IsTargetAvailable")]
    private static IEnumerable<CodeInstruction> IsTargetAvailableTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var isAlreadyReservedMethod = AccessTools.DeclaredMethod(
            "Shashlichnik.WorkGiver_GoDownIfJobUnderground:IsAlreadyReserved");
        var replacementMethod = MpMethodUtil.MethodOf(IsAlreadyReservedForPawn);
        var patched = false;

        // Use a 2-instruction lookahead buffer so we can inspect the instructions that lead
        // into the callvirt and transform them on the fly without collecting the whole list.
        CodeInstruction prev2 = null;
        CodeInstruction prev1 = null;

        foreach (var current in instructions)
        {
            if (!patched && current.Calls(isAlreadyReservedMethod))
            {
                // Before `callvirt IsAlreadyReserved` the compiler emits:
                //   ldarg.0   (push 'this' for the virtual call)
                //   ldarg.1   (push 'target' argument)
                //   callvirt  IsAlreadyReserved
                //
                // We want instead:
                //   ldarg.1   (keep 'target' on stack)
                //   ldarg.s 4 (push 'forPawn' — 5th method argument)
                //   call      IsAlreadyReservedForPawn
                //
                // So replace the ldarg.0 (prev2) with a nop, keep ldarg.1 (prev1), insert
                // an ldarg.s 4 for forPawn, then switch callvirt → call.

                // Emit nop in place of ldarg.0 (prev2), preserving any jump labels/blocks.
                var nop = new CodeInstruction(OpCodes.Nop);
                if (prev2 != null)
                {
                    nop.labels.AddRange(prev2.labels);
                    nop.blocks.AddRange(prev2.blocks);
                }
                yield return nop;

                // Keep ldarg.1 (target) as-is.
                if (prev1 != null)
                    yield return prev1;

                // Push forPawn (arg index 4: this=0, target=1, map=2, startPos=3, forPawn=4).
                yield return new CodeInstruction(OpCodes.Ldarg_S, (byte)4);

                // Replace the callvirt with a direct call to our static helper.
                var call = new CodeInstruction(OpCodes.Call, replacementMethod);
                call.labels.AddRange(current.labels);
                call.blocks.AddRange(current.blocks);
                yield return call;

                patched = true;
                prev2 = null;
                prev1 = null;
                continue;
            }

            // Normal path: flush the oldest buffered instruction and shift.
            if (prev2 != null)
                yield return prev2;
            prev2 = prev1;
            prev1 = current;
        }

        // Flush remaining buffer.
        if (prev2 != null) yield return prev2;
        if (prev1 != null) yield return prev1;

        if (!patched)
            Log.Warning($"[MP Compat] Failed to patch Faction.OfPlayer in {__originalMethod.FullDescription()}.");
    }

    // WorkGiver_GoDownIfJobUnderground.TryFindFirstAvailableJobTargetAt accesses the cave
    // (underground pocket) map's designationManager, areaManager, and other per-faction managers
    // via caveExit.Map. However, ExecuteCmd only pushes faction context onto the *surface* map.
    // Per-faction managers (designations, areas, haul destinations, etc.) are stored per map in
    // FactionMapData. Without pushing the correct faction onto the cave map, its managers point
    // to whichever faction was last active there — which differs between host and client.
    // This causes PotentialTargetsUnderground (mine designations), IsTargetAvailable (allowed
    // areas, haulables), and reachability checks to return different results per machine.
    //
    // Fix: push forPawn's faction onto the cave map before the method runs, pop in finalizer.
    // Uses force:true because the global FactionContext already equals forPawn.Faction (set by
    // ExecuteCmd) — without force, Push would no-op and skip the per-map manager swap.
    [MpCompatPrefix("Shashlichnik.WorkGiver_GoDownIfJobUnderground", "TryFindFirstAvailableJobTargetAt")]
    private static void TryFindFirstAvailableJobTargetAtPrefix(Thing caveExit, Pawn forPawn)
    {
        PushFactionContext(caveExit?.Map, forPawn?.Faction);
    }

    [MpCompatFinalizer("Shashlichnik.WorkGiver_GoDownIfJobUnderground", "TryFindFirstAvailableJobTargetAt")]
    private static void TryFindFirstAvailableJobTargetAtFinalizer(Thing caveExit)
    {
        PopFactionContext(caveExit?.Map);
    }

    private static void PushFactionContext(Map map, Faction faction)
    {
        if (map == null || faction == null)
            return;

        if (pushFactionMethod == null)
        {
            if (!missingPushFactionWarningLogged)
            {
                missingPushFactionWarningLogged = true;
                Log.Warning("[MP Compat] Missing Multiplayer.Client.Factions.FactionExtensions.PushFaction(Map,Faction,bool); cave workgiver patch will run without cave-map faction context swap.");
            }
            return;
        }

        pushFactionMethod.Invoke(null, new object[] { map, faction, true });
    }

    private static void PopFactionContext(Map map)
    {
        if (map == null)
            return;

        if (popFactionMethod == null)
        {
            if (!missingPopFactionWarningLogged)
            {
                missingPopFactionWarningLogged = true;
                Log.Warning("[MP Compat] Missing Multiplayer.Client.Factions.FactionExtensions.PopFaction(Map); cave workgiver patch will run without cave-map faction context restore.");
            }
            return;
        }

        popFactionMethod.Invoke(null, new object[] { map });
    }

    // Replacement for WorkGiver_GoDownIfJobUnderground.IsAlreadyReserved that uses the mining
    // pawn's own faction instead of Faction.OfPlayer, making the reservation check produce the
    // same result on all multiplayer clients.
    private static bool IsAlreadyReservedForPawn(Thing target, Pawn forPawn)
    {
        if (target?.Map == null)
            return false;
        Pawn reserver = null;
        return target.Map.reservationManager?.TryGetReserver(target, forPawn.Faction, out reserver) ?? false;
    }

    // WorkGiver_GoDownIfJobUnderground_HaulToPortal.PotentialTargetsUnderground uses
    // PawnsInFaction(Faction.OfPlayer) to enumerate colonists currently hauling to the portal.
    // In multiplayer Faction.OfPlayer returns different factions on each client, so the
    // "already loading" count differs per machine — some targets appear available on one client
    // but not on another, causing TryFindFirstAvailableJobTargetAt to diverge.
    //
    // Fix: replace PawnsInFaction(Faction.OfPlayer) with FreeColonistsSpawned, which returns
    // colonists from ALL player factions consistently on every client.
    [MpCompatTranspiler("Shashlichnik.WorkGiver_GoDownIfJobUnderground_HaulToPortal", "PotentialTargetsUnderground")]
    private static IEnumerable<CodeInstruction> PotentialTargetsUndergroundTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        var factionOfPlayerGetter = AccessTools.PropertyGetter(typeof(Faction), nameof(Faction.OfPlayer));
        var pawnsInFactionMethod = AccessTools.Method(typeof(MapPawns), nameof(MapPawns.PawnsInFaction));
        var freeColonistsSpawnedGetter = AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.FreeColonistsSpawned));
        var patched = false;

        CodeInstruction prev = null;

        foreach (var current in instructions)
        {
            if (!patched && prev != null && prev.Calls(factionOfPlayerGetter) && current.Calls(pawnsInFactionMethod))
            {
                // Before:  call Faction.get_OfPlayer()  /  callvirt MapPawns.PawnsInFaction(Faction)
                // After:   nop                          /  callvirt MapPawns.get_FreeColonistsSpawned()

                // Replace Faction.get_OfPlayer() with nop (stack already has MapPawns).
                var nop = new CodeInstruction(OpCodes.Nop);
                nop.labels.AddRange(prev.labels);
                nop.blocks.AddRange(prev.blocks);
                yield return nop;

                // Replace PawnsInFaction with FreeColonistsSpawned (no Faction arg needed).
                var replacement = new CodeInstruction(OpCodes.Callvirt, freeColonistsSpawnedGetter);
                replacement.labels.AddRange(current.labels);
                replacement.blocks.AddRange(current.blocks);
                yield return replacement;

                patched = true;
                prev = null;
                continue;
            }

            if (prev != null)
                yield return prev;
            prev = current;
        }

        if (prev != null)
            yield return prev;

        if (!patched)
            Log.Warning($"[MP Compat] Failed to patch Faction.OfPlayer in {__originalMethod.FullDescription()}.");
    }

    [MpCompatTranspiler("Shashlichnik.GenStep_DeepDiver", "TrySpawnInterestAt")]
    private static IEnumerable<CodeInstruction> TrySpawnInterestAtTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var currentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));
        var patched = false;

        foreach (var instruction in instructions)
        {
            if (!patched && instruction.Calls(currentMapGetter))
            {
                // TrySpawnInterestAt(Map map, IntVec3 thingPos) — map is arg 1 on this instance method.
                yield return new CodeInstruction(OpCodes.Ldarg_1)
                {
                    labels = instruction.labels,
                    blocks = instruction.blocks,
                };
                patched = true;
                continue;
            }

            yield return instruction;
        }

        if (!patched)
            Log.Warning($"[MP Compat] Failed to patch Find.CurrentMap in {__originalMethod.FullDescription()}.");
    }

    [MpCompatTranspiler("Shashlichnik.CaveMapComponent", "ProcessCollapsing")]
    private static IEnumerable<CodeInstruction> ProcessCollapsingTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var currentMapCheckCount = 0;

        foreach (var instruction in instructions)
        {
            if (instruction.Calls(findCurrentMapGetter))
            {
                currentMapCheckCount++;

                // Keep the first check (camera/audio), but force the landslide roll to use this component's map.
                if (currentMapCheckCount == 2)
                {
                    var loadComponent = new CodeInstruction(OpCodes.Ldarg_0)
                    {
                        // Preserve jump targets/exception block metadata from replaced instruction.
                        labels = instruction.labels,
                        blocks = instruction.blocks,
                    };

                    yield return loadComponent;
                    yield return new CodeInstruction(OpCodes.Call, MpMethodUtil.MethodOf(GetMapFromComponent));
                    continue;
                }
            }

            yield return instruction;
        }

        if (currentMapCheckCount < 2)
            Log.Warning($"[MP Compat] Expected 2 Find.CurrentMap checks in {__originalMethod.FullDescription()}, found {currentMapCheckCount}.");
    }

    private static Map GetMapFromComponent(MapComponent component) => component.map;
}
