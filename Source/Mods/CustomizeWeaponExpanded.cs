using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Custom Weapons Expanded by Feliperathal</summary>
[MpCompatFor("Feliperathal.CustomizeWeaponExpanded")]
internal class CustomizeWeaponExpanded
{
    public CustomizeWeaponExpanded(ModContentPack mod)
        => LongEventHandler.ExecuteWhenFinished(LatePatch);

    private static void LatePatch()
        => MpCompatPatchLoader.LoadPatch<CustomizeWeaponExpanded>();

    [MpCompatPrefix("CWE.HarmonyPatches.EnemyWeapon", "Postfix")]
    private static void PreRandomizeGeneratedWeaponTraits(Pawn pawn, out bool __state)
    {
        __state = MP.IsInMultiplayer;

        if (!__state || pawn == null)
            return;

        Rand.PushState(GetGeneratedWeaponSeed(pawn));
    }

    [MpCompatFinalizer("CWE.HarmonyPatches.EnemyWeapon", "Postfix")]
    private static void PostRandomizeGeneratedWeaponTraits(bool __state)
    {
        if (__state)
            Rand.PopState();
    }

    private static int GetGeneratedWeaponSeed(Pawn pawn)
    {
        var seed = pawn.thingIDNumber;
        var weapon = pawn.equipment?.Primary;

        if (weapon != null)
            seed = Gen.HashCombineInt(seed, weapon.thingIDNumber);

        return seed;
    }
}

