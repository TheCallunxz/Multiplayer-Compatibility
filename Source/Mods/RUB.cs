using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>RUB Weapon Box</summary>
[MpCompatFor("osg.rub")]
internal class RUB
{
    public RUB(ModContentPack mod)
        => MpCompatPatchLoader.LoadPatch<RUB>();

    [MpCompatPrefix("RUB.CompUseEffect_RUB", "DoEffect")]
    private static void PreUseEffect(object __instance, Pawn usedBy, out bool __state)
    {
        __state = MP.IsInMultiplayer;

        if (!__state || __instance is not ThingComp comp)
            return;

        var seed = comp.parent.thingIDNumber;
        if (usedBy != null)
            seed = Gen.HashCombineInt(seed, usedBy.thingIDNumber);

        Rand.PushState(seed);
    }

    [MpCompatFinalizer("RUB.CompUseEffect_RUB", "DoEffect")]
    private static void PostUseEffect(bool __state)
    {
        if (__state)
            Rand.PopState();
    }
}

