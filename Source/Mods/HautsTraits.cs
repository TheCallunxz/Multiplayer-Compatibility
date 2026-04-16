using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.Sound;

namespace Multiplayer.Compat;

/// <summary>Hauts' Traits by Hautarche</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3422847391"/>
[MpCompatFor("hautarche.hautstraits")]
internal class HautsTraits
{
    // TraitSerumWindow fields
    private static AccessTools.FieldRef<object, Pawn> serumWindowPawnField;
    private static AccessTools.FieldRef<object, TraitDef> serumWindowChosenTraitField;
    private static AccessTools.FieldRef<object, int> serumWindowChosenDegreeField;

    // chargeSource is HautsFramework.Comp_ItemCharged which extends ThingComp.
    // We store it as object since the type is not directly referenceable.
    private static Type compItemChargedType;
    private static AccessTools.FieldRef<object, int> chargeCompRemainingChargesField;
    private static FastInvokeHandler usedOnceMethod;

    // We need a way to get the chargeSource from the window.
    // The field type is Comp_ItemCharged, use FieldInfo for safe access.
    private static FieldInfo serumWindowChargeSourceFieldInfo;

    public HautsTraits(ModContentPack mod)
    {
        var windowType = AccessTools.TypeByName("HautsTraits.TraitSerumWindow");
        serumWindowPawnField = AccessTools.FieldRefAccess<Pawn>(windowType, "pawn");
        serumWindowChosenTraitField = AccessTools.FieldRefAccess<TraitDef>(windowType, "chosenTrait");
        serumWindowChosenDegreeField = AccessTools.FieldRefAccess<int>(windowType, "chosenTraitDegree");
        serumWindowChargeSourceFieldInfo = AccessTools.Field(windowType, "chargeSource");

        compItemChargedType = AccessTools.TypeByName("HautsFramework.Comp_ItemCharged");
        chargeCompRemainingChargesField = AccessTools.FieldRefAccess<int>(compItemChargedType, "remainingCharges");
        usedOnceMethod = MethodInvoker.GetHandler(AccessTools.Method(compItemChargedType, "UsedOnce"));

        MpCompatPatchLoader.LoadPatch<HautsTraits>();
    }

    private static ThingComp GetChargeSource(object windowInstance)
        => serumWindowChargeSourceFieldInfo.GetValue(windowInstance) as ThingComp;

    #region PreOpen RNG isolation

    // PreOpen uses RandomElement for PersoneuroformatterScrambler traits.
    // This runs during window init and consumes Rand in UI context,
    // which would diverge RNG state between clients.
    [MpCompatPrefix("HautsTraits.TraitSerumWindow", "PreOpen")]
    private static void PrePreOpen()
    {
        if (MP.IsInMultiplayer)
            Rand.PushState();
    }

    [MpCompatFinalizer("HautsTraits.TraitSerumWindow", "PreOpen")]
    private static void PostPreOpen()
    {
        if (MP.IsInMultiplayer)
            Rand.PopState();
    }

    #endregion

    #region DoWindowContents sync

    // The OK button in DoWindowContents directly mutates shared state:
    // - pawn.story.traits.GainTrait (adds a trait)
    // - chargeSource.UsedOnce × N (consumes charges, may destroy item)
    // - pawn.skills.GetSkill(skill).Level += amount (adjusts skills)
    // We snapshot state before, inflate charges to prevent destroyOnEmpty,
    // then revert all local mutations and sync the action.

    [MpCompatPrefix("HautsTraits.TraitSerumWindow", nameof(Window.DoWindowContents))]
    private static void PreDoWindowContents(object __instance, out SerumWindowState __state)
    {
        __state = default;

        if (!MP.IsInMultiplayer)
            return;

        // Isolate any UI RNG from shared simulation state.
        Rand.PushState();

        // During synced execution, let the mutations happen normally.
        if (MP.IsExecutingSyncCommand)
            return;

        var pawn = serumWindowPawnField(__instance);
        if (pawn?.story?.traits == null || pawn.skills == null)
            return;

        var chargeComp = GetChargeSource(__instance);
        if (chargeComp == null)
            return;

        __state.active = true;
        __state.traitCount = pawn.story.traits.allTraits.Count;

        // Inflate charges so UsedOnce never triggers destroyOnEmpty.
        // This prevents the item from being destroyed during local preview.
        __state.savedCharges = chargeCompRemainingChargesField(chargeComp);
        chargeCompRemainingChargesField(chargeComp) = __state.savedCharges + 100;

        // Save skill levels so we can revert skill changes from trait application.
        __state.savedSkillLevels = new Dictionary<SkillDef, int>();
        foreach (var rec in pawn.skills.skills)
            __state.savedSkillLevels[rec.def] = rec.Level;
    }

    [MpCompatFinalizer("HautsTraits.TraitSerumWindow", nameof(Window.DoWindowContents))]
    private static void PostDoWindowContents(object __instance, SerumWindowState __state)
    {
        if (!MP.IsInMultiplayer)
            return;

        Rand.PopState();

        if (!__state.active)
            return;

        var pawn = serumWindowPawnField(__instance);
        var chargeComp = GetChargeSource(__instance);

        // Calculate how many charges were consumed locally
        // (inflated - current = number of UsedOnce calls).
        int currentCharges = chargeCompRemainingChargesField(chargeComp);
        int chargesConsumed = (__state.savedCharges + 100) - currentCharges;

        // Always restore charges to original value.
        chargeCompRemainingChargesField(chargeComp) = __state.savedCharges;

        bool traitWasApplied = pawn.story.traits.allTraits.Count > __state.traitCount;

        if (traitWasApplied)
        {
            var chosenTrait = serumWindowChosenTraitField(__instance);
            var chosenDegree = serumWindowChosenDegreeField(__instance);

            // Revert the locally-applied trait.
            var gained = pawn.story.traits.GetTrait(chosenTrait, chosenDegree);
            if (gained != null)
                pawn.story.traits.RemoveTrait(gained);

            // Revert skill levels.
            foreach (var rec in pawn.skills.skills)
                if (__state.savedSkillLevels.TryGetValue(rec.def, out var level))
                    rec.Level = level;

            // Dispatch the real action through sync so all clients apply it.
            SyncedApplyTraitSerum(pawn, chosenTrait, chosenDegree,
                chargeComp.parent, chargesConsumed);
        }
        else
        {
            // No trait was applied — just restore skills in case anything touched them.
            foreach (var rec in pawn.skills.skills)
                if (__state.savedSkillLevels.TryGetValue(rec.def, out var level))
                    rec.Level = level;
        }
    }

    [MpCompatSyncMethod(cancelIfAnyArgNull = true)]
    private static void SyncedApplyTraitSerum(
        Pawn pawn, TraitDef traitDef, int degree,
        Thing chargeSourceThing, int chargesConsumed)
    {
        if (pawn?.story?.traits == null || traitDef == null)
            return;

        // Apply the trait.
        var trait = new Trait(traitDef, degree, false);
        pawn.story.traits.GainTrait(trait, true);

        // Consume charges.
        if (chargeSourceThing != null && !chargeSourceThing.Destroyed)
        {
            ThingComp chargeComp = null;
            foreach (var comp in chargeSourceThing.AllComps)
            {
                if (compItemChargedType.IsInstanceOfType(comp))
                {
                    chargeComp = comp;
                    break;
                }
            }

            if (chargeComp != null)
            {
                for (int i = 0; i < chargesConsumed; i++)
                    usedOnceMethod(chargeComp);
            }
        }

        // Apply skill gains from the trait's degree data.
        var data = traitDef.DataAtDegree(degree);
        if (data?.skillGains != null && pawn.skills != null)
        {
            foreach (var entry in data.skillGains)
                pawn.skills.GetSkill(entry.skill).Level += entry.amount;
        }

        // Sound + message — safe to run on all clients during sync.
        SoundDefOf.MechSerumUsed.PlayOneShot(SoundInfo.InMap(pawn));
    }

    private struct SerumWindowState
    {
        public bool active;
        public int traitCount;
        public int savedCharges;
        public Dictionary<SkillDef, int> savedSkillLevels;
    }

    #endregion
}


