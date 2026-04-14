using System;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Weapon Fitting by Deko, Ancot</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3535852001"/>
[MpCompatFor("deko.weaponfitting")]
internal class WeaponFitting
{
    private static FastInvokeHandler ancotGameComponentGetter;
    private static AccessTools.FieldRef<object, List<Thing>> ancotStarWeaponField;

    private static AccessTools.FieldRef<object, Thing> renameDialogWeaponField;
    private static FastInvokeHandler uniqueNameMethod;
    private static FastInvokeHandler setUniqueNameMethod;

    public WeaponFitting(ModContentPack mod)
    {
        var gameComponentType = AccessTools.TypeByName("AncotLibrary.GameComponent_AncotLibrary");
        ancotGameComponentGetter = MethodInvoker.GetHandler(AccessTools.PropertyGetter(gameComponentType, "GC"));
        ancotStarWeaponField = AccessTools.FieldRefAccess<List<Thing>>(gameComponentType, "starWeaponCached");

        var renameDialogType = AccessTools.TypeByName("AncotLibrary.Dialog_NameWeapon");
        renameDialogWeaponField = AccessTools.FieldRefAccess<Thing>(renameDialogType, "weapon");
        uniqueNameMethod = MethodInvoker.GetHandler(AccessTools.Method("AncotLibrary.WeaponTraitsUtility:UniqueName", [typeof(Thing)]));
        setUniqueNameMethod = MethodInvoker.GetHandler(AccessTools.Method("AncotLibrary.WeaponTraitsUtility:SetUniqueName", [typeof(Thing), typeof(string)]));

        MpCompatPatchLoader.LoadPatch<WeaponFitting>();
    }

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


