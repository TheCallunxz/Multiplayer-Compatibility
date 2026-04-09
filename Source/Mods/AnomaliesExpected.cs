using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Anomalies Expected by MrHydralisk</summary>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3399875765"/>
[MpCompatFor("MrHydralisk.AnomaliesExpected")]
internal class AnomaliesExpected
{
    private static FieldInfo entityEntryThingDefField;
    private static FieldInfo entityEntryCodexEntryField;
    private static MethodInfo entityEntrySpawnThingMethod;
    private static FieldInfo gameComponentInstanceField;
    private static MethodInfo getEntityEntryFromThingDefMethod;
    private static MethodInfo getEntityEntryFromEntityCodexEntryDefMethod;
    private static FieldInfo entityDatabaseAnomalyDialogCompField;

    [MpCompatSyncField("AnomaliesExpected.Comp_EntityDatabaseAnomaly", "selectedIncidentDef")]
    private static ISyncField selectedIncidentField = null;

    public AnomaliesExpected(ModContentPack mod)
        => LongEventHandler.ExecuteWhenFinished(LatePatch);

    private static void LatePatch()
    {
        var aeEntityEntryType = AccessTools.TypeByName("AnomaliesExpected.AEEntityEntry");
        var gameComponentType = AccessTools.TypeByName("AnomaliesExpected.GameComponent_AnomaliesExpected");
        var dialogEntityDatabaseAnomalyType = AccessTools.TypeByName("AnomaliesExpected.Dialog_AEEntityDatabaseAnomaly");

        if (aeEntityEntryType == null || gameComponentType == null || dialogEntityDatabaseAnomalyType == null)
        {
            Log.Warning("MPCompat :: Anomalies Expected - failed to resolve one or more runtime types");
            return;
        }

        entityEntryThingDefField = AccessTools.Field(aeEntityEntryType, "ThingDef");
        entityEntryCodexEntryField = AccessTools.Field(aeEntityEntryType, "EntityCodexEntryDef");
        entityEntrySpawnThingMethod = AccessTools.Method(aeEntityEntryType, "SpawnThing");
        gameComponentInstanceField = AccessTools.Field(gameComponentType, "instance");
        getEntityEntryFromThingDefMethod = AccessTools.Method(gameComponentType, "GetEntityEntryFromThingDef");
        getEntityEntryFromEntityCodexEntryDefMethod = AccessTools.Method(gameComponentType, "GetEntityEntryFromEntityCodexEntryDef");
        entityDatabaseAnomalyDialogCompField = AccessTools.Field(dialogEntityDatabaseAnomalyType, "entityDatabaseAnomaly");

        if (entityEntryThingDefField == null || entityEntryCodexEntryField == null || entityEntrySpawnThingMethod == null ||
            gameComponentInstanceField == null || getEntityEntryFromThingDefMethod == null ||
            getEntityEntryFromEntityCodexEntryDefMethod == null || entityDatabaseAnomalyDialogCompField == null)
        {
            Log.Warning("MPCompat :: Anomalies Expected - failed to resolve one or more runtime members");
            return;
        }

        MpCompatPatchLoader.LoadPatch<AnomaliesExpected>();

        // Hospital bed: sign + donor mode toggle.
        var type = AccessTools.TypeByName("AnomaliesExpected.Comp_AnomalyHospitalBed");
        MpCompat.RegisterLambdaDelegate(type, nameof(ThingComp.CompGetGizmosExtra), 0, 2);

        // Atmospheric controller/cooler: +/- temp methods + reset button.
        type = AccessTools.TypeByName("AnomaliesExpected.Comp_AtmosphericController");
        MP.RegisterSyncMethod(type, "InterfaceChangeTargetTemperature");
        MpCompat.RegisterLambdaDelegate(type, nameof(ThingComp.CompGetGizmosExtra), 0);

        type = AccessTools.TypeByName("AnomaliesExpected.Comp_AtmosphericCooler");
        MP.RegisterSyncMethod(type, "InterfaceChangeTargetTemperature");
        MpCompat.RegisterLambdaDelegate(type, nameof(ThingComp.CompGetGizmosExtra), 0);

        // Speedometer: keep the mod's own gizmos/UI, only sync the final actions.
        // 0 = open level float menu, 1 = apply chosen level, 2 = open confirmation, 3 = confirmed removal.
        type = AccessTools.TypeByName("AnomaliesExpected.Hediff_SpeedometerLevel");
        MpCompat.RegisterLambdaDelegate(type, nameof(Hediff.GetGizmos), 1, 3);
    }

    [MpCompatPrefix("AnomaliesExpected.Dialog_AEEntityDatabaseAnomaly", nameof(Window.DoWindowContents))]
    private static void PreEntityDatabaseAnomalyDialog(object __instance, out ThingComp __state)
    {
        __state = null;

        if (!MP.IsInMultiplayer)
            return;

        Rand.PushState();

        __state = entityDatabaseAnomalyDialogCompField.GetValue(__instance) as ThingComp;
        if (__state != null)
        {
            MP.WatchBegin();
            selectedIncidentField.Watch(__state);
        }
    }

    [MpCompatFinalizer("AnomaliesExpected.Dialog_AEEntityDatabaseAnomaly", nameof(Window.DoWindowContents))]
    private static void PostEntityDatabaseAnomalyDialog(ThingComp __state)
    {
        if (!MP.IsInMultiplayer)
            return;

        if (__state != null)
            MP.WatchEnd();

        Rand.PopState();
    }

    [MpCompatTranspiler("AnomaliesExpected.Dialog_AEEntityDB", "EntityRecord")]
    private static IEnumerable<CodeInstruction> ReplaceSpawnThingCall(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        => instructions.ReplaceMethod(entityEntrySpawnThingMethod,
            to: MpMethodUtil.MethodOf(SpawnThingWithSync),
            baseMethod: __originalMethod,
            expectedReplacements: 1);

    private static void SpawnThingWithSync(object entry, ThingDef thingDef, Thing parent)
    {
        if (MP.IsInMultiplayer)
            SyncedSpawnThing(entry, thingDef, parent);
        else
            entityEntrySpawnThingMethod.Invoke(entry, new object[] { thingDef, parent });
    }

    [MpCompatSyncMethod]
    private static void SyncedSpawnThing(object entry, ThingDef thingDef, Thing parent)
    {
        if (entry == null || thingDef == null)
            return;

        entityEntrySpawnThingMethod.Invoke(entry, new object[] { thingDef, parent });
    }

    [MpCompatSyncWorker("AnomaliesExpected.AEEntityEntry")]
    private static void SyncAEEntityEntry(SyncWorker sync, ref object entry)
    {
        if (sync.isWriting)
        {
            sync.Write(entry != null);
            if (entry == null)
                return;

            sync.Write(entityEntryThingDefField.GetValue(entry) as ThingDef);
            sync.Write(entityEntryCodexEntryField.GetValue(entry) as EntityCodexEntryDef);
        }
        else
        {
            if (!sync.Read<bool>())
            {
                entry = null;
                return;
            }

            var thingDef = sync.Read<ThingDef>();
            var codexEntry = sync.Read<EntityCodexEntryDef>();
            entry = ResolveEntityEntry(thingDef, codexEntry);
        }
    }

    private static object ResolveEntityEntry(ThingDef thingDef, EntityCodexEntryDef codexEntry)
    {
        var component = gameComponentInstanceField.GetValue(null);
        if (component == null)
            return null;

        object entry = null;

        if (thingDef != null)
            entry = getEntityEntryFromThingDefMethod.Invoke(component, new object[] { thingDef });

        if (entry == null && codexEntry != null)
            entry = getEntityEntryFromEntityCodexEntryDefMethod.Invoke(component, new object[] { codexEntry });

        if (entry == null)
            return null;

        var resolvedThingDef = entityEntryThingDefField.GetValue(entry) as ThingDef;
        var resolvedCodexEntry = entityEntryCodexEntryField.GetValue(entry) as EntityCodexEntryDef;

        if (thingDef != null && resolvedThingDef != thingDef)
            return null;

        if (codexEntry != null && resolvedCodexEntry != codexEntry)
            return getEntityEntryFromEntityCodexEntryDefMethod.Invoke(component, new object[] { codexEntry });

        return entry;
    }
}





