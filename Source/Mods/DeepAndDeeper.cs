using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Deep And Deeper by Shashlichnik</summary>
[MpCompatFor("Shashlichnik.DeepAndDeeper")]
internal class DeepAndDeeper
{
    private static readonly MethodInfo findCurrentMapGetter = AccessTools.PropertyGetter(typeof(Find), nameof(Find.CurrentMap));

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
