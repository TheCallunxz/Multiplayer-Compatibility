using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Anomalies Expected by MrHydralisk</summary>d
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3399875765"/>
[MpCompatFor("MrHydralisk.AnomaliesExpected")]
internal class AnomaliesExpected
{
    private static MethodInfo entityEntrySpawnThingBaseMethod;
    private static FastInvokeHandler entityEntrySpawnThingMethod;
    private static AccessTools.FieldRef<object, ThingDef> entityEntryThingDefField;
    private static AccessTools.FieldRef<object, EntityCodexEntryDef> entityEntryCodexEntryField;
    private static AccessTools.FieldRef<object, ThingComp> entityDatabaseAnomalyDialogCompField;

    private static AccessTools.FieldRef<object> gameComponentInstanceField;
    private static FastInvokeHandler getEntityEntryFromThingDefMethod;
    private static FastInvokeHandler getEntityEntryFromEntityCodexEntryDefMethod;

    [MpCompatSyncField("AnomaliesExpected.Comp_EntityDatabaseAnomaly", "selectedIncidentDef")]
    private static ISyncField selectedIncidentField = null;

    public AnomaliesExpected(ModContentPack mod)
    {
        var aeEntityEntryType = AccessTools.TypeByName("AnomaliesExpected.AEEntityEntry");
        var gameComponentType = AccessTools.TypeByName("AnomaliesExpected.GameComponent_AnomaliesExpected");
        var dialogEntityDatabaseAnomalyType = AccessTools.TypeByName("AnomaliesExpected.Dialog_AEEntityDatabaseAnomaly");

        entityEntryThingDefField = AccessTools.FieldRefAccess<ThingDef>(aeEntityEntryType, "ThingDef");
        entityEntryCodexEntryField = AccessTools.FieldRefAccess<EntityCodexEntryDef>(aeEntityEntryType, "EntityCodexEntryDef");
        entityEntrySpawnThingBaseMethod = AccessTools.Method(aeEntityEntryType, "SpawnThing");
        entityEntrySpawnThingMethod = MethodInvoker.GetHandler(entityEntrySpawnThingBaseMethod);
        gameComponentInstanceField = AccessTools.StaticFieldRefAccess<object>(AccessTools.Field(gameComponentType, "instance"));
        getEntityEntryFromThingDefMethod = MethodInvoker.GetHandler(AccessTools.Method(gameComponentType, "GetEntityEntryFromThingDef"));
        getEntityEntryFromEntityCodexEntryDefMethod = MethodInvoker.GetHandler(AccessTools.Method(gameComponentType, "GetEntityEntryFromEntityCodexEntryDef"));
        entityDatabaseAnomalyDialogCompField = AccessTools.FieldRefAccess<ThingComp>(dialogEntityDatabaseAnomalyType, "entityDatabaseAnomaly");

        MpCompatPatchLoader.LoadPatch<AnomaliesExpected>();

        // Hospital bed: sign + donor mode toggle.
        var type = AccessTools.TypeByName("AnomaliesExpected.Comp_AnomalyHospitalBed");
        MP.RegisterSyncDelegateLambda(type, nameof(ThingComp.CompGetGizmosExtra), 0);
        MP.RegisterSyncDelegateLambda(type, nameof(ThingComp.CompGetGizmosExtra), 2);

        // Atmospheric controller/cooler: +/- temp methods + reset button.
        type = AccessTools.TypeByName("AnomaliesExpected.Comp_AtmosphericController");
        MP.RegisterSyncMethod(type, "InterfaceChangeTargetTemperature");
        MP.RegisterSyncDelegateLambda(type, nameof(ThingComp.CompGetGizmosExtra), 0);

        type = AccessTools.TypeByName("AnomaliesExpected.Comp_AtmosphericCooler");
        MP.RegisterSyncMethod(type, "InterfaceChangeTargetTemperature");
        MP.RegisterSyncDelegateLambda(type, nameof(ThingComp.CompGetGizmosExtra), 0);

        // Speedometer: keep the mod's own gizmos/UI, only sync the final actions.
        // 0 = open level float menu, 1 = apply chosen level, 2 = open confirmation, 3 = confirmed removal.
        type = AccessTools.TypeByName("AnomaliesExpected.Hediff_SpeedometerLevel");
        MP.RegisterSyncDelegateLambda(type, nameof(Hediff.GetGizmos), 1);
        MP.RegisterSyncDelegateLambda(type, nameof(Hediff.GetGizmos), 3);
    }

    [MpCompatPrefix("AnomaliesExpected.Dialog_AEEntityDatabaseAnomaly", nameof(Window.DoWindowContents))]
    private static void PreEntityDatabaseAnomalyDialog(object __instance, out ThingComp __state)
    {
        __state = null;

        if (!MP.IsInMultiplayer)
            return;

        Rand.PushState();

        __state = entityDatabaseAnomalyDialogCompField(__instance);
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
        => instructions.ReplaceMethod(entityEntrySpawnThingBaseMethod,
            to: MpMethodUtil.MethodOf(SpawnThingWithSync),
            baseMethod: __originalMethod,
            expectedReplacements: 1);

    private static void SpawnThingWithSync(object entry, ThingDef thingDef, Thing parent)
    {
        if (MP.IsInMultiplayer)
            SyncedSpawnThing(entry, thingDef, parent);
        else
            entityEntrySpawnThingMethod(entry, thingDef, parent);
    }

    [MpCompatSyncMethod]
    private static void SyncedSpawnThing(object entry, ThingDef thingDef, Thing parent)
    {
        if (entry == null || thingDef == null)
            return;

        entityEntrySpawnThingMethod(entry, thingDef, parent);
    }

    [MpCompatSyncWorker("AnomaliesExpected.AEEntityEntry")]
    private static void SyncAEEntityEntry(SyncWorker sync, ref object entry)
    {
        if (sync.isWriting)
        {
            sync.Write(entry != null);
            if (entry == null)
                return;

            sync.Write(entityEntryThingDefField(entry));
            sync.Write(entityEntryCodexEntryField(entry));
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
        var component = gameComponentInstanceField();
        if (component == null)
            return null;

        object entry = null;

        if (thingDef != null)
            entry = getEntityEntryFromThingDefMethod(component, thingDef);

        if (entry == null && codexEntry != null)
            entry = getEntityEntryFromEntityCodexEntryDefMethod(component, codexEntry);

        if (entry == null)
            return null;

        var resolvedThingDef = entityEntryThingDefField(entry);
        var resolvedCodexEntry = entityEntryCodexEntryField(entry);

        if (thingDef != null && resolvedThingDef != thingDef)
            return null;

        if (codexEntry != null && resolvedCodexEntry != codexEntry)
            return getEntityEntryFromEntityCodexEntryDefMethod(component, codexEntry);

        return entry;
    }
}





