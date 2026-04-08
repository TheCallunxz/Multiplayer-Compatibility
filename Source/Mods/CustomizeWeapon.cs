using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Customize Weapon by Vortex</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3453832412"/>
    [MpCompatFor("Vortex.CustomizeWeapon")]
    internal class CustomizeWeapon
    {
        public CustomizeWeapon(ModContentPack mod)
            => LongEventHandler.ExecuteWhenFinished(LatePatch);

        private static void LatePatch()
        {
            // Main weapon panel gizmo on the ground
            MpCompat.RegisterLambdaMethod("CWF.CompDynamicTraits", nameof(ThingComp.CompGetGizmosExtra), 0);

            // Main weapon panel gizmo while equipped
            MpCompat.RegisterLambdaMethod("CWF.CompDynamicTraits", "CompGetEquippedGizmosExtra", 0);

            // Dev-only color randomizer gizmo
            MpCompat.RegisterLambdaMethod("CWF.CompColorable", nameof(ThingComp.CompGetGizmosExtra), 0).SetDebugOnly();
        }
    }
}
