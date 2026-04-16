using Verse;

namespace Multiplayer.Compat;

/// <summary>Integrated Implants by LTS</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3223443793"/>
[MpCompatFor("lts.I")]
internal class IntegratedImplants
{
    public IntegratedImplants(ModContentPack mod)
    {
        // Audit notes:
        // - LTS_FloatMenuOptionProvider_ExtractImplant ends in vanilla TryTakeOrderedJob with no extra shared-state mutation.
        // - LTS_HediffComp_RemoteGizmo returns vanilla Command_Ability gizmos.
        // - The real MP risks are two gameplay Harmony patches that instantiate System.Random during shared simulation.
        PatchingUtilities.PatchSystemRand(new[]
        {
            "LTS_Implants.CompDevourer_CompTick_Patch:CompTickPostfix",
            "LTS_Implants.HediffComp_ReactOnDamage_Notify_PawnPostApplyDamage_Patch_EMP:HediffComp_ReactOnDamage_Notify_PawnPostApplyDamage_Prefix",
        }, false);
    }
}
