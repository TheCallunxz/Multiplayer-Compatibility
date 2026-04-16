using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Weapon Fitting by Deko, Ancot</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3535852001"/>
[MpCompatFor("deko.weaponfitting")]
internal class WeaponFitting
{
    // --- Existing fields ---
    private static FastInvokeHandler ancotGameComponentGetter;
    private static AccessTools.FieldRef<object, List<Thing>> ancotStarWeaponField;

    private static AccessTools.FieldRef<object, Thing> renameDialogWeaponField;
    private static FastInvokeHandler uniqueNameMethod;
    private static FastInvokeHandler setUniqueNameMethod;

    // --- Auto-detect *_Unique weapons (reflection into AncotLibrary / WeaponFitting) ---
    private static Type compPropsUniqueWeaponType;
    private static Type compPropsEmptyUniqueWeaponType;
    private static Type compPropsEquippableAbilityType;
    private static Type compPropsEquippableAbilityReloadableType;
    private static FieldInfo uniqueWeaponCategoriesField; // List<WeaponCategoryDef>
    private static FieldInfo maxTraitsField;              // int
    private static FastInvokeHandler hasUniqueCompMethod; // WF_Utility.HasUniqueComp(ThingDef)

    public WeaponFitting(ModContentPack mod)
    {
        // ---- Existing setup ----
        var gameComponentType = AccessTools.TypeByName("AncotLibrary.GameComponent_AncotLibrary");
        ancotGameComponentGetter = MethodInvoker.GetHandler(AccessTools.PropertyGetter(gameComponentType, "GC"));
        ancotStarWeaponField = AccessTools.FieldRefAccess<List<Thing>>(gameComponentType, "starWeaponCached");

        var renameDialogType = AccessTools.TypeByName("AncotLibrary.Dialog_NameWeapon");
        renameDialogWeaponField = AccessTools.FieldRefAccess<Thing>(renameDialogType, "weapon");
        uniqueNameMethod = MethodInvoker.GetHandler(AccessTools.Method("AncotLibrary.WeaponTraitsUtility:UniqueName", [typeof(Thing)]));
        setUniqueNameMethod = MethodInvoker.GetHandler(AccessTools.Method("AncotLibrary.WeaponTraitsUtility:SetUniqueName", [typeof(Thing), typeof(string)]));

        // ---- Auto-detect *_Unique weapons ----
        compPropsUniqueWeaponType = AccessTools.TypeByName("AncotLibrary.CompProperties_UniqueWeapon");
        compPropsEmptyUniqueWeaponType = AccessTools.TypeByName("AncotLibrary.CompProperties_EmptyUniqueWeapon");
        compPropsEquippableAbilityType = AccessTools.TypeByName("AncotLibrary.CompProperties_EquippableAbility");
        compPropsEquippableAbilityReloadableType = AccessTools.TypeByName("AncotLibrary.CompProperties_EquippableAbilityReloadable");

        if (compPropsUniqueWeaponType != null)
            uniqueWeaponCategoriesField = AccessTools.Field(compPropsUniqueWeaponType, "weaponCategories");
        if (compPropsEmptyUniqueWeaponType != null)
            maxTraitsField = AccessTools.Field(compPropsEmptyUniqueWeaponType, "max_traits");

        hasUniqueCompMethod = MethodInvoker.GetHandler(AccessTools.Method("WeaponFitting.WF_Utility:HasUniqueComp"));

        // Patch WF_weaponPatch postfix to run auto-detect after the vanilla XML pass
        var wfWeaponPatchMethod = AccessTools.Method("WeaponFitting.ThingGenerator_WeaponFittings:WF_weaponPatch");
        if (wfWeaponPatchMethod != null)
        {
            var harmony = new Harmony("mp.compat.weaponfitting.autodetect");
            harmony.Patch(wfWeaponPatchMethod,
                postfix: new HarmonyMethod(typeof(WeaponFitting), nameof(PostWFWeaponPatch)));
        }

        // Weapon Fitting applies its own def mutations from a startup static constructor.
        // Compat can load after that one-shot pass already ran, so queue a late fallback
        // scan as well instead of relying on the postfix alone.
        LongEventHandler.ExecuteWhenFinished(PostWFWeaponPatch);

        MpCompatPatchLoader.LoadPatch<WeaponFitting>();
    }

    // -------------------------------------------------------------------------
    // Auto-detect *_Unique weapons
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs immediately after WF_weaponPatch. Scans every loaded ThingDef whose
    /// defName ends with "_Unique", finds the corresponding base weapon, and
    /// injects a CompProperties_EmptyUniqueWeapon so fittings can be applied to it —
    /// mirroring exactly what the vanilla XML-driven pass does for registered weapons.
    /// </summary>
    private static void PostWFWeaponPatch()
    {
        if (compPropsUniqueWeaponType == null || compPropsEmptyUniqueWeaponType == null
            || uniqueWeaponCategoriesField == null || maxTraitsField == null
            || hasUniqueCompMethod == null)
            return;

        // Resolve the modification ThingCategoryDef lazily (defs are ready by now)
        var ancotDefOfType = AccessTools.TypeByName("AncotLibrary.AncotDefOf");
        ThingCategoryDef modCategory = null;
        if (ancotDefOfType != null)
        {
            var modCatField = AccessTools.Field(ancotDefOfType, "Ancot_WeaponsModification");
            modCategory = modCatField?.GetValue(null) as ThingCategoryDef;
        }

        const string suffix = "_Unique";

        foreach (var uniqueDef in DefDatabase<ThingDef>.AllDefs)
        {
            if (!uniqueDef.defName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (uniqueDef.comps == null)
                continue;

            var baseDefName = uniqueDef.defName.Substring(0, uniqueDef.defName.Length - suffix.Length);
            var baseDef = DefDatabase<ThingDef>.GetNamedSilentFail(baseDefName);
            if (baseDef == null)
                continue;

            if (baseDef.comps == null)
                baseDef.comps = new List<CompProperties>();

            // Skip if the base weapon already has fitting support
            if ((bool)hasUniqueCompMethod(null, baseDef))
                continue;

            // Find CompProperties_UniqueWeapon on the *_Unique def
            CompProperties uniqueProps = null;
            foreach (var comp in uniqueDef.comps)
            {
                if (compPropsUniqueWeaponType.IsInstanceOfType(comp))
                {
                    uniqueProps = comp;
                    break;
                }
            }
            if (uniqueProps == null)
                continue;

            // Get weapon categories (List<WeaponCategoryDef>) from the _Unique weapon
            var categories = uniqueWeaponCategoriesField.GetValue(uniqueProps) as IList;
            if (categories == null || categories.Count == 0)
            {
                Log.Warning("[WeaponFitting/MpCompat] '" + uniqueDef.defName
                    + "' has no weaponCategories — skipping auto-registration of '" + baseDefName + "'.");
                continue;
            }

            // Guard: scan base weapon comps for existing unique comp
            bool alreadyHasUnique = false;
            bool hasAbility = false;
            CompProperties compEquippable = null;

            foreach (var compP in baseDef.comps)
            {
                if (compPropsEmptyUniqueWeaponType.IsInstanceOfType(compP)
                    || compPropsUniqueWeaponType.IsInstanceOfType(compP))
                {
                    alreadyHasUnique = true;
                    break;
                }
                if (compP.compClass == typeof(CompEquippable))
                    compEquippable = compP;
                if (compPropsEquippableAbilityType != null
                    && compPropsEquippableAbilityType.IsInstanceOfType(compP))
                    hasAbility = true;
            }
            if (alreadyHasUnique)
                continue;

            // Replace CompEquippable with CompEquippableAbilityReloadable if not already able
            if (!hasAbility && compEquippable != null && compPropsEquippableAbilityReloadableType != null)
            {
                baseDef.comps.Remove(compEquippable);
                baseDef.comps.Add((CompProperties)Activator.CreateInstance(compPropsEquippableAbilityReloadableType));
            }

            // Build and inject a CompProperties_EmptyUniqueWeapon with 3 trait slots
            var emptyComp = (CompProperties)Activator.CreateInstance(compPropsEmptyUniqueWeaponType);
            maxTraitsField.SetValue(emptyComp, 3);
            // Share the same List<WeaponCategoryDef> instance — it is read-only after def loading
            uniqueWeaponCategoriesField.SetValue(emptyComp, categories);
            baseDef.comps.Add(emptyComp);

            // Add to the weapon modification browse category
            if (modCategory != null)
            {
                if (baseDef.thingCategories == null)
                    baseDef.thingCategories = new List<ThingCategoryDef>();

                if (!baseDef.thingCategories.Contains(modCategory))
                    baseDef.thingCategories.Add(modCategory);

                if (modCategory.childThingDefs != null && !modCategory.childThingDefs.Contains(baseDef))
                    modCategory.childThingDefs.Add(baseDef);
            }

            Log.Message("[WeaponFitting/MpCompat] Auto-registered '" + baseDefName
                + "' for fittings via '" + uniqueDef.defName + "'.");
        }
    }


    // -------------------------------------------------------------------------
    // Existing patches (star toggle, rename dialog)
    // -------------------------------------------------------------------------

    [MpCompatPrefix("WeaponFitting.ThingColumnWorker_Star", "ClickedIcon")]
    private static bool PreToggleStar(Thing thing)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return true;

        SyncedToggleStar(thing);
        return false;
    }

    [MpCompatSyncMethod]
    private static void SyncedToggleStar(Thing thing)
    {
        if (thing == null)
            return;

        var gameComponent = ancotGameComponentGetter(null);
        if (gameComponent == null)
            return;

        var starWeapons = ancotStarWeaponField(gameComponent);
        if (starWeapons == null)
            return;

        if (starWeapons.Contains(thing))
            starWeapons.Remove(thing);
        else
            starWeapons.Add(thing);
    }

    [MpCompatPrefix("AncotLibrary.Dialog_NameWeapon", nameof(Window.DoWindowContents))]
    private static void PreRenameDialog(object __instance, out string __state)
    {
        __state = null;

        if (!MP.IsInMultiplayer)
            return;

        var weapon = renameDialogWeaponField(__instance);
        if (weapon != null)
            __state = GetUniqueName(weapon);
    }

    [MpCompatPostfix("AncotLibrary.Dialog_NameWeapon", nameof(Window.DoWindowContents))]
    private static void PostRenameDialog(object __instance, string __state)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return;

        var weapon = renameDialogWeaponField(__instance);
        if (weapon == null)
            return;

        var currentName = GetUniqueName(weapon);
        if (currentName == __state)
            return;

        SetUniqueName(weapon, __state);
        SyncedRenameWeapon(weapon, currentName);
    }

    [MpCompatSyncMethod]
    private static void SyncedRenameWeapon(Thing weapon, string name)
    {
        if (weapon == null)
            return;

        SetUniqueName(weapon, name);
    }

    private static string GetUniqueName(Thing weapon)
        => weapon == null ? null : (string)uniqueNameMethod(null, weapon);

    private static void SetUniqueName(Thing weapon, string name)
        => setUniqueNameMethod(null, weapon, name);
}

