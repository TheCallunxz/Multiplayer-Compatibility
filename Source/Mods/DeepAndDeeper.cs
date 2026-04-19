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
        // Gizmos that change shared state.
        MpCompat.RegisterLambdaMethod("Shashlichnik.CaveEntrance", "GetGizmos", 2);
        MpCompat.RegisterLambdaMethod("Shashlichnik.CaveExit", "GetGizmos", 1);

        // Dev-only cave controls.
        MpCompat.RegisterLambdaMethod("Shashlichnik.CaveEntrance", "GetGizmos", 3, 4).SetDebugOnly();
        MP.RegisterSyncMethod(AccessTools.DeclaredMethod("Shashlichnik.DebugActions:MoveAllPawns")).SetDebugOnly();

        // Uses Find.CurrentMap during map generation while a map argument is available.
        PatchingUtilities.ReplaceCurrentMapUsage("Shashlichnik.GenStep_DeepDiver:TrySpawnInterestAt");

        // Local camera/ambient visual paths call Rand and can desync RNG state.
        PatchingUtilities.PatchPushPopRand(new[]
        {
            "Shashlichnik.CaveMapComponent:ProcessAmbient",
            "Shashlichnik.CaveEntrance:Tick",
        });

        MpCompatPatchLoader.LoadPatch<DeepAndDeeper>();
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
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
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

