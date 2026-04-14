using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Anomalies Expected Addon by Fallen</summary>
[MpCompatFor("fallen.anomaliesexpectedaddon")]
internal class AnomaliesExpectedAddon
{
    public AnomaliesExpectedAddon(ModContentPack mod)
    {
        // Dev-only gizmo that directly spawns a pawn via a local Command_Action lambda.
        // Normal spawning/ability logic runs during shared simulation and doesn't need compat.
        MpCompat.RegisterLambdaMethod("AEAddon.AECompSpawnerPawn", nameof(ThingComp.CompGetGizmosExtra), 0)
            .SetDebugOnly();
    }
}

