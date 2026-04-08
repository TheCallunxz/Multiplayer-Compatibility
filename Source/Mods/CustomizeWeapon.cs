using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Customize Weapon by Vortex</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3453832412"/>
    [MpCompatFor("Vortex.CustomizeWeapon")]
    internal class CustomizeWeapon
    {
        private static Type modificationDataType;
        private static Type modificationTypeType;

        private static FieldInfo weaponWindowSessionField;
        private static FieldInfo sessionWeaponField;
        private static MethodInfo sessionCalculateNetChangesMethod;
        private static ConstructorInfo jobDispatcherConstructor;
        private static MethodInfo dispatchMethod;
        private static FieldInfo modificationDataTypeField;
        private static FieldInfo modificationDataPartField;
        private static FieldInfo modificationDataTraitField;
        private static FieldInfo modificationDataModuleDefField;

        private static bool suppressLocalDispatch;

        public CustomizeWeapon(ModContentPack mod)
            => LongEventHandler.ExecuteWhenFinished(LatePatch);

        private static void LatePatch()
        {
            var weaponWindowType = AccessTools.TypeByName("CWF.WeaponWindow");
            var modificationSessionType = AccessTools.TypeByName("CWF.WeaponModificationSession");
            var jobDispatcherType = AccessTools.TypeByName("CWF.Controllers.JobDispatcher");
            modificationDataType = AccessTools.TypeByName("CWF.ModificationData");
            modificationTypeType = AccessTools.TypeByName("CWF.ModificationType");

            if (weaponWindowType == null || modificationSessionType == null || jobDispatcherType == null ||
                modificationDataType == null || modificationTypeType == null)
            {
                Log.Warning("MPCompat :: Customize Weapon - failed to resolve one or more runtime types");
                return;
            }

            weaponWindowSessionField = AccessTools.Field(weaponWindowType, "_session");
            sessionWeaponField = AccessTools.Field(modificationSessionType, "_weapon");
            sessionCalculateNetChangesMethod = AccessTools.Method(modificationSessionType, "CalculateNetChanges");
            jobDispatcherConstructor = AccessTools.Constructor(jobDispatcherType, new[] { typeof(Thing) });
            dispatchMethod = AccessTools.Method(jobDispatcherType, "Dispatch");
            modificationDataTypeField = AccessTools.Field(modificationDataType, "Type");
            modificationDataPartField = AccessTools.Field(modificationDataType, "Part");
            modificationDataTraitField = AccessTools.Field(modificationDataType, "Trait");
            modificationDataModuleDefField = AccessTools.Field(modificationDataType, "ModuleDef");

            if (weaponWindowSessionField == null || sessionWeaponField == null || sessionCalculateNetChangesMethod == null ||
                jobDispatcherConstructor == null || dispatchMethod == null || modificationDataTypeField == null ||
                modificationDataPartField == null || modificationDataTraitField == null ||
                modificationDataModuleDefField == null)
            {
                Log.Warning("MPCompat :: Customize Weapon - failed to resolve one or more runtime members");
                return;
            }

            MP.RegisterSyncMethod(typeof(CustomizeWeapon), nameof(SyncedDispatch));

            MpCompat.harmony.Patch(AccessTools.Method(weaponWindowType, nameof(Window.PostClose)),
                prefix: new HarmonyMethod(typeof(CustomizeWeapon), nameof(PreWeaponWindowPostClose)));
            MpCompat.harmony.Patch(dispatchMethod,
                prefix: new HarmonyMethod(typeof(CustomizeWeapon), nameof(PreJobDispatcherDispatch)));
        }

        private static void PreWeaponWindowPostClose(object __instance)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return;

            var session = weaponWindowSessionField.GetValue(__instance);
            var weapon = sessionWeaponField.GetValue(session) as Thing;

            if (weapon == null)
                return;

            var changes = ExtractNetChanges(sessionCalculateNetChangesMethod.Invoke(session, null));

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

        private static bool PreJobDispatcherDispatch()
            => !suppressLocalDispatch || !MP.IsInMultiplayer || MP.IsExecutingSyncCommand;

        private static void SyncedDispatch(Thing weapon, List<(int changeType, Def part, Def trait, ThingDef moduleDef)> changes)
        {
            var dispatcher = jobDispatcherConstructor.Invoke(new object[] { weapon });
            dispatchMethod.Invoke(dispatcher, new[] { CreateNetChanges(changes) });
        }

        private static List<(int changeType, Def part, Def trait, ThingDef moduleDef)> ExtractNetChanges(object netChanges)
        {
            var result = new List<(int changeType, Def part, Def trait, ThingDef moduleDef)>();

            if (netChanges is not IEnumerable enumerable)
                return result;

            foreach (var change in enumerable)
            {
                result.Add((
                    Convert.ToInt32(modificationDataTypeField.GetValue(change)),
                    (Def)modificationDataPartField.GetValue(change),
                    (Def)modificationDataTraitField.GetValue(change),
                    (ThingDef)modificationDataModuleDefField.GetValue(change)
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
                modificationDataTypeField.SetValue(change, Enum.ToObject(modificationTypeType, changeType));
                modificationDataPartField.SetValue(change, part);
                modificationDataTraitField.SetValue(change, trait);
                modificationDataModuleDefField.SetValue(change, moduleDef);
                list.Add(change);
            }

            return list;
        }
    }
}
