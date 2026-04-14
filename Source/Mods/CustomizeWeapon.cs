using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Customize Weapon by Vortex</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3453832412"/>
[MpCompatFor("Vortex.CustomizeWeapon")]
internal class CustomizeWeapon
{
    private static Type modificationDataType;
    private static Type modificationTypeType;
    private static Type jobDispatcherType;

    private static AccessTools.FieldRef<object, object> weaponWindowSessionField;
    private static AccessTools.FieldRef<object, Thing> sessionWeaponField;
    private static FastInvokeHandler sessionCalculateNetChangesMethod;
    private static FastInvokeHandler dispatchMethod;
    private static AccessTools.FieldRef<object, object> modificationDataTypeField;
    private static AccessTools.FieldRef<object, Def> modificationDataPartField;
    private static AccessTools.FieldRef<object, Def> modificationDataTraitField;
    private static AccessTools.FieldRef<object, ThingDef> modificationDataModuleDefField;

    private static bool suppressLocalDispatch;

    public CustomizeWeapon(ModContentPack mod)
    {
        var weaponWindowType = AccessTools.TypeByName("CWF.WeaponWindow");
        var modificationSessionType = AccessTools.TypeByName("CWF.WeaponModificationSession");
        jobDispatcherType = AccessTools.TypeByName("CWF.Controllers.JobDispatcher");
        modificationDataType = AccessTools.TypeByName("CWF.ModificationData");
        modificationTypeType = AccessTools.TypeByName("CWF.ModificationType");

        weaponWindowSessionField = AccessTools.FieldRefAccess<object>(weaponWindowType, "_session");
        sessionWeaponField = AccessTools.FieldRefAccess<Thing>(modificationSessionType, "_weapon");
        sessionCalculateNetChangesMethod = MethodInvoker.GetHandler(AccessTools.Method(modificationSessionType, "CalculateNetChanges"));
        dispatchMethod = MethodInvoker.GetHandler(AccessTools.Method(jobDispatcherType, "Dispatch"));
        modificationDataTypeField = AccessTools.FieldRefAccess<object>(modificationDataType, "Type");
        modificationDataPartField = AccessTools.FieldRefAccess<Def>(modificationDataType, "Part");
        modificationDataTraitField = AccessTools.FieldRefAccess<Def>(modificationDataType, "Trait");
        modificationDataModuleDefField = AccessTools.FieldRefAccess<ThingDef>(modificationDataType, "ModuleDef");

        MpCompatPatchLoader.LoadPatch<CustomizeWeapon>();
    }

    [MpCompatPrefix("CWF.WeaponWindow", nameof(Window.PostClose))]
    private static void PreWeaponWindowPostClose(object __instance)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return;

        var session = weaponWindowSessionField(__instance);
        if (session == null)
            return;

        var weapon = sessionWeaponField(session);
        if (weapon == null)
            return;

        var changes = ExtractNetChanges(sessionCalculateNetChangesMethod(session));
        if (changes.Count == 0)
            return;

        suppressLocalDispatch = true;
        try
        {
            SyncedDispatch(weapon, changes);
        }
        finally
        {
            suppressLocalDispatch = false;
        }
    }

    [MpCompatPrefix("CWF.Controllers.JobDispatcher", "Dispatch")]
    private static bool PreJobDispatcherDispatch()
        => !suppressLocalDispatch || !MP.IsInMultiplayer || MP.IsExecutingSyncCommand;

    [MpCompatPrefix("CWF.HarmonyPatches.Postfix_PawnWeaponGenerator_TryGenerateWeaponFor", "Postfix")]
    private static void PreRandomizeGeneratedWeaponTraits(Pawn pawn, out bool __state)
    {
        __state = MP.IsInMultiplayer;

        if (!__state || pawn == null)
            return;

        Rand.PushState(GetGeneratedWeaponSeed(pawn));
    }

    [MpCompatFinalizer("CWF.HarmonyPatches.Postfix_PawnWeaponGenerator_TryGenerateWeaponFor", "Postfix")]
    private static void PostRandomizeGeneratedWeaponTraits(bool __state)
    {
        if (__state)
            Rand.PopState();
    }

    [MpCompatSyncMethod]
    private static void SyncedDispatch(Thing weapon, List<(int changeType, Def part, Def trait, ThingDef moduleDef)> changes)
    {
        var dispatcher = Activator.CreateInstance(jobDispatcherType, weapon);
        dispatchMethod(dispatcher, CreateNetChanges(changes));
    }

    private static List<(int changeType, Def part, Def trait, ThingDef moduleDef)> ExtractNetChanges(object netChanges)
    {
        var result = new List<(int changeType, Def part, Def trait, ThingDef moduleDef)>();

        if (netChanges is not IEnumerable enumerable)
            return result;

        foreach (var change in enumerable)
        {
            result.Add((
                Convert.ToInt32(modificationDataTypeField(change)),
                modificationDataPartField(change),
                modificationDataTraitField(change),
                modificationDataModuleDefField(change)
            ));
        }

        return result;
    }

    private static object CreateNetChanges(IEnumerable<(int changeType, Def part, Def trait, ThingDef moduleDef)> changes)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(modificationDataType));

        foreach (var (changeType, part, trait, moduleDef) in changes)
        {
            var change = Activator.CreateInstance(modificationDataType);
            modificationDataTypeField(change) = Enum.ToObject(modificationTypeType, changeType);
            modificationDataPartField(change) = part;
            modificationDataTraitField(change) = trait;
            modificationDataModuleDefField(change) = moduleDef;
            list.Add(change);
        }

        return list;
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
