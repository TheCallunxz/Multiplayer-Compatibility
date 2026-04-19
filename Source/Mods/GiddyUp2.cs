using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>Giddy-Up 2 by Roolo, Owlchemist</summary>
    /// <see href="https://github.com/Owlchemist/GiddyUp2"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2934245647"/>
    [MpCompatFor("Owlchemist.GiddyUp")]
    [MpCompatFor("MemeGoddess.GiddyUp")]
    public class GiddyUp2
    {
        private const string WaitForRiderJobDriver = "GiddyUpRideAndRoll.Jobs.JobDriver_WaitForRider";

        // Multiplayer
        private static FastInvokeHandler transferableAdjustTo;

        // ExtendedPawnData/ExtendedDataStorage
        private static AccessTools.FieldRef<object, Pawn> extendedPawnDataPawn;
        private static AccessTools.FieldRef<object, Pawn> extendedPawnDataMount;
        private static FastInvokeHandler getExtendedPawnData;

        // Patch_TransferableOneWayWidget
        private static AccessTools.FieldRef<object, object> parentClass;
        private static AccessTools.FieldRef<object, TransferableOneWay> transferableField;

        // Designator
        private static AccessTools.FieldRef<object, Area> designatorSelectedArea;
        private static AccessTools.FieldRef<object, string> designatorAreaLabel;

        private static readonly string[] RideAndRollPawnGizmoTypes =
        {
            "GiddyUpCore.RideAndRoll.Harmony.Pawn_GetGizmos",
            "GiddyUpRideAndRoll.Harmony.Pawn_GetGizmos",
        };

        private const string SaddleUpPawnGizmoType = "SaddleUp.Pawn_GetGizmos_SU2";

        public GiddyUp2(ModContentPack mod)
        {
            MpCompatPatchLoader.LoadPatch<GiddyUp2>();

            // Gizmos
            {
                // Release animals
                MpCompat.RegisterLambdaDelegate("GiddyUp.Harmony.Patch_PawnGetGizmos", "Postfix", 0);

                // Stop waiting for rider (namespace changed to GiddyUpCore.RideAndRoll.Harmony)
                // Delay this registration until loading has finished so accessing the patch type can't trip
                // its translation-dependent initialization before RimWorld has an active language.
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    RegisterRideAndRollGizmoSync();
                    RegisterSaddleUpGizmoSync();
                });
            }

            // FloatMenus
            {
                // Dismount without hitching / Dismount / Mount / Switch mount
                MpCompat.RegisterLambdaDelegate("GiddyUp.Harmony.FloatMenuUtility", "AddMountingOptions", 0, 1, 2, 3);

                // Select/clear rider pawn for caravan
                var type = AccessTools.TypeByName("GiddyUpCaravan.Harmony.Patch_TransferableOneWayWidget");
                MP.RegisterSyncMethod(type, "SelectMountRider");
                MP.RegisterSyncMethod(type, "ClearMountRider");

                // Sync changes to TransferableOneWay.CountToTransfer
                var method = MpMethodUtil.GetLambda(type, "HandleAnimal", lambdaOrdinal: 0);
                transferableField = AccessTools.FieldRefAccess<TransferableOneWay>(method.DeclaringType, "trad");
                MpCompat.harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(GiddyUp2), nameof(WatchTranferableCount)));

                method = MpMethodUtil.GetLambda(type, "HandleAnimal", lambdaOrdinal: 1);
                parentClass = AccessTools.FieldRefAccess<object>(method.DeclaringType, "CS$<>8__locals1");
                MpCompat.harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(GiddyUp2), nameof(PreSetRider)));
            }

            // PawnColumnWorker
            {
                MP.RegisterSyncMethod(AccessTools.TypeByName("GiddyUpRideAndRoll.PawnColumnWorker_Mountable_Colonists"), "SetValue");
                MP.RegisterSyncMethod(AccessTools.TypeByName("GiddyUpRideAndRoll.PawnColumnWorker_Mountable_Slaves"), "SetValue");
                MP.RegisterSyncMethod(AccessTools.TypeByName("GiddyUpRideAndRoll.PawnColumnWorker_AllowedToRide"), "SetValue");
            }
            // Sync
            {
                // ExtendedPawnData — field renamed from "pawn" to "_pawn"
                var type = AccessTools.TypeByName("GiddyUp.ExtendedPawnData");
                extendedPawnDataPawn = AccessTools.FieldRefAccess<Pawn>(type, "_pawn");
                extendedPawnDataMount = AccessTools.FieldRefAccess<Pawn>(type, "_mount");
                MP.RegisterSyncWorker<object>(SyncExtendedPawnData, type);

                // Method renamed from GetGUData to GetExtendedPawnData
                getExtendedPawnData = MethodInvoker.GetHandler(AccessTools.DeclaredMethod("GiddyUp.StorageUtility:GetExtendedPawnData"));
                transferableAdjustTo = MethodInvoker.GetHandler(AccessTools.DeclaredMethod("Multiplayer.Client.SyncFields:TransferableAdjustTo"));

                // Designator_GU — SelectedArea is now an auto-property, _areaLabel is now readonly
                type = AccessTools.TypeByName("GiddyUp.Designator_GU");
                designatorSelectedArea = AccessTools.FieldRefAccess<Area>(type, "<SelectedArea>k__BackingField");
                designatorAreaLabel = AccessTools.FieldRefAccess<string>(type, "_areaLabel");
                // Designator_GU has an argument for the constructor which would fail with shouldConstruct, but it's only
                // used by the subclasses which have parameterless ones (they provide the argument themselves).
                MP.RegisterSyncWorker<Designator>(SyncGiddyUpDesignator, type, isImplicit: true, shouldConstruct: true);
            }

            // Current map usage
            {
                // Ride-and-Roll uses current map in a map component tick path (reported by unpatched scan).
                // Keep behavior scoped to that component's map in MP to avoid per-client camera/map divergence.
                PatchingUtilities.ReplaceCurrentMapUsage("GiddyUpCore.SaddleUp.Coordinator:MapComponentTick", false, false);
            }
        }

        private static void WatchTranferableCount(TransferableOneWay ___trad)
            => transferableAdjustTo(null, ___trad);

        private static void PreSetRider(object __instance)
            => WatchTranferableCount(transferableField(parentClass(__instance)));

        private static void RegisterRideAndRollGizmoSync()
        {
            foreach (var typeName in RideAndRollPawnGizmoTypes)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                    continue;

                // Some versions expose a helper method, others only contain the gizmo lambda.
                MP.RegisterSyncMethod(type, "PawnEndCurrentJob");
                MpCompat.RegisterLambdaDelegate(type, "Postfix", 0);
            }
        }

        private static void RegisterSaddleUpGizmoSync()
        {
            var type = AccessTools.TypeByName(SaddleUpPawnGizmoType);
            if (type == null)
                return;

            // Mount gizmos are built through helper methods with version-dependent lambda shapes.
            // Try common methods/ordinals so sync survives minor method-body changes.
            TryRegisterLambdaDelegates(type, "Postfix", 0, 1, 2, 3);
            TryRegisterLambdaDelegates(type, "CreateGizmo_ToggleMount", 0, 1, 2, 3);
            TryRegisterLambdaDelegates(type, "CreateGizmo_GoAndMount", 0, 1, 2, 3);
            TryRegisterLambdaDelegates(type, "CreateGizmo_Dismount", 0, 1, 2, 3);
        }

        private static void TryRegisterLambdaDelegate(Type type, string methodName, int lambdaOrdinal)
        {
            try
            {
                MpCompat.RegisterLambdaDelegate(type, methodName, lambdaOrdinal);
            }
            catch
            {
                // Different Giddy-Up builds expose different compiler-generated lambdas.
            }
        }

        private static void TryRegisterLambdaDelegates(Type type, string methodName, params int[] lambdaOrdinals)
        {
            foreach (var lambdaOrdinal in lambdaOrdinals)
                TryRegisterLambdaDelegate(type, methodName, lambdaOrdinal);
        }

        private static void SyncExtendedPawnData(SyncWorker sync, ref object extendedPawnData)
        {
            if (sync.isWriting)
                sync.Write(extendedPawnDataPawn(extendedPawnData));
            else
            {
                var pawn = sync.Read<Pawn>();
                extendedPawnData = getExtendedPawnData(null, pawn);
            }
        }

        private static void SyncGiddyUpDesignator(SyncWorker sync, ref Designator designator)
        {
            if (sync.isWriting)
            {
                sync.Write(designatorSelectedArea(designator));
                sync.Write(designatorAreaLabel(designator));
            }
            else
            {
                designatorSelectedArea(designator) = sync.Read<Area>();
                designatorAreaLabel(designator) = sync.Read<string>();
            }
        }

        [MpCompatPrefix("GiddyUp.MountUtility", "FindPlaceToDismount")]
        private static void PreFindPlaceToDismount(Pawn rider, IntVec3 riderDestination, out bool __state)
        {
            __state = MP.IsInMultiplayer;

            if (!__state)
                return;

            Rand.PushState(GetStableRandSeed(rider, riderDestination.GetHashCode()));
        }

        [MpCompatFinalizer("GiddyUp.MountUtility", "FindPlaceToDismount")]
        private static void PostFindPlaceToDismount(bool __state)
        {
            if (__state)
                Rand.PopState();
        }

        [MpCompatPrefix(typeof(JobDriver), nameof(JobDriver.DriverTick))]
        private static void PreDriverTick(JobDriver __instance, out bool __state)
        {
            __state = MP.IsInMultiplayer && __instance?.GetType().FullName == WaitForRiderJobDriver;

            if (!__state)
                return;

            Rand.PushState(GetStableRandSeed(__instance.pawn));
        }

        [MpCompatFinalizer(typeof(JobDriver), nameof(JobDriver.DriverTick))]
        private static void PostDriverTick(bool __state)
        {
            if (__state)
                Rand.PopState();
        }

        private static int GetStableRandSeed(Pawn pawn, int extraSeed = 0)
        {
            var seed = Find.TickManager.TicksGame;

            if (pawn != null)
                seed = Gen.HashCombineInt(seed, pawn.thingIDNumber);

            if (extraSeed != 0)
                seed = Gen.HashCombineInt(seed, extraSeed);

            return seed;
        }

        [MpCompatPrefix("GiddyUp.Harmony.Pawn_DrawTracker_DrawPos", "DrawOffset")]
        private static bool PreDrawOffset(Pawn_DrawTracker __instance, ref Vector3 __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var pawn = __instance?.pawn;
            if (pawn == null)
                return true;

            var pawnData = getExtendedPawnData(null, pawn);

            // DrawOffset runs in render code; avoid local-only gameplay mutation from its null-mount failsafe.
            // Shared job logic already has its own mounted-state sanity checks during simulation ticks.
            if (pawnData == null || extendedPawnDataMount(pawnData) == null)
            {
                __result = Vector3.zero;
                return false;
            }

            return true;
        }
    }
}

