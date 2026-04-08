using System;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Custom Weapons Expanded by Feliperathal</summary>
    [MpCompatFor("Feliperathal.CustomizeWeaponExpanded")]
    internal class CustomizeWeaponExpanded
    {
        private static MethodInfo randomizeExpandedEnemyWeaponTraitsMethod;

        public CustomizeWeaponExpanded(ModContentPack mod)
            => LongEventHandler.ExecuteWhenFinished(LatePatch);

        private static void LatePatch()
        {
            randomizeExpandedEnemyWeaponTraitsMethod = AccessTools.DeclaredMethod("CWE.HarmonyPatches.EnemyWeapon:Postfix")
                ?? AccessTools.Method("CWE.HarmonyPatches.EnemyWeapon:Postfix");

            if (randomizeExpandedEnemyWeaponTraitsMethod == null)
            {
                Log.Warning("MPCompat :: Custom Weapons Expanded - failed to resolve CWE.HarmonyPatches.EnemyWeapon.Postfix");
                return;
            }

            MpCompat.harmony.Patch(randomizeExpandedEnemyWeaponTraitsMethod,
                prefix: new HarmonyMethod(typeof(CustomizeWeaponExpanded), nameof(PreRandomizeGeneratedWeaponTraits)),
                finalizer: new HarmonyMethod(typeof(CustomizeWeaponExpanded), nameof(PostRandomizeGeneratedWeaponTraits)));
        }

        private static void PreRandomizeGeneratedWeaponTraits(Pawn pawn, out bool __state)
        {
            __state = MP.IsInMultiplayer;

            if (!__state || pawn == null)
                return;

            Rand.PushState(GetGeneratedWeaponSeed(pawn));
        }

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
}

