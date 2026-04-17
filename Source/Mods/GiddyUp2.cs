using System.Collections.Generic;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
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
        private const string MountedJobDefName = "Mounted";
        private const string DragonJumpAbilityType = "DD.Ability_DragonJump";
        private const string DragonJumpFlyerType = "DD.AbilityDragonStraightPawnFlyer";
        private const string WingedFlyerAbilityType = "DD.Ability_WingedFlyer";
        private const string WingedFlyerType = "DD.WingedFlyer";

        // Multiplayer
        private static FastInvokeHandler transferableAdjustTo;
        private static FastInvokeHandler setAllowedMountedJob;

        private static Pawn bypassMountedAnimalStartJobPawn;
        private static Pawn preserveMountedAnimalDuringAbility;
        private static Pawn preserveMountedRiderDuringAbility;
        private static readonly Dictionary<Pawn, Pawn> pendingFlyerRidersByAnimal = [];

        private sealed class MountedAbilityStartState
        {
            public Pawn PreviousBypassPawn;
            public Pawn PreviousPreservedAnimal;
            public Pawn PreviousPreservedRider;
        }

        // ExtendedPawnData/ExtendedDataStorage
        private static AccessTools.FieldRef<object, Pawn> extendedPawnDataPawn;
        private static FastInvokeHandler getExtendedPawnData;

        // Patch_TransferableOneWayWidget
        private static AccessTools.FieldRef<object, object> parentClass;
        private static AccessTools.FieldRef<object, TransferableOneWay> transferableField;

        // Designator
        private static AccessTools.FieldRef<object, Area> designatorSelectedArea;
        private static AccessTools.FieldRef<object, string> designatorAreaLabel;

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
                LongEventHandler.ExecuteWhenFinished(RegisterRideAndRollGizmoSync);
                LongEventHandler.ExecuteWhenFinished(AllowMountedAbilityJobs);
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

                type = AccessTools.TypeByName("GiddyUp.Jobs.JobDriver_Mounted");
                setAllowedMountedJob = MethodInvoker.GetHandler(AccessTools.DeclaredMethod(type, "SetAllowedJob"));
            }
        }

        private static void WatchTranferableCount(TransferableOneWay ___trad)
            => transferableAdjustTo(null, ___trad);

        private static void PreSetRider(object __instance)
            => WatchTranferableCount(transferableField(parentClass(__instance)));

        private static void RegisterRideAndRollGizmoSync()
        {
            var type = AccessTools.TypeByName("GiddyUpCore.RideAndRoll.Harmony.Pawn_GetGizmos");

            if (type != null)
                MP.RegisterSyncMethod(type, "PawnEndCurrentJob");
        }

        private static void AllowMountedAbilityJobs()
        {
            if (setAllowedMountedJob == null)
                return;

            var useVerbOnThing = DefDatabase<JobDef>.GetNamedSilentFail("UseVerbOnThing");
            if (useVerbOnThing != null)
                setAllowedMountedJob(null, useVerbOnThing, true);
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

        [MpCompatPrefix(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
        private static void PreStartJob(Pawn_JobTracker __instance, Job newJob, out MountedAbilityStartState __state)
        {
            __state = null;

            if (!ShouldPreserveMountedAnimalForAbility(__instance, newJob, out var rider))
                return;

            __state = new MountedAbilityStartState
            {
                PreviousBypassPawn = bypassMountedAnimalStartJobPawn,
                PreviousPreservedAnimal = preserveMountedAnimalDuringAbility,
                PreviousPreservedRider = preserveMountedRiderDuringAbility,
            };

            bypassMountedAnimalStartJobPawn = __instance.pawn;
            preserveMountedAnimalDuringAbility = __instance.pawn;
            preserveMountedRiderDuringAbility = rider;

            if (IsDragonFlyAbility(newJob))
                pendingFlyerRidersByAnimal[__instance.pawn] = rider;

            __instance.jobQueue.EnqueueFirst(new Job(__instance.curJob.def, rider) { count = 1 });
        }

        [MpCompatFinalizer(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
        private static void PostStartJob(MountedAbilityStartState __state)
        {
            if (__state == null)
                return;

            bypassMountedAnimalStartJobPawn = __state.PreviousBypassPawn;
            preserveMountedAnimalDuringAbility = __state.PreviousPreservedAnimal;
            preserveMountedRiderDuringAbility = __state.PreviousPreservedRider;
        }

        [MpCompatPrefix("GiddyUp.Harmony.Patch_StartJob", "Prefix")]
        private static bool PreGiddyUpPatchStartJob(Pawn_JobTracker __instance, ref bool __result)
        {
            if (bypassMountedAnimalStartJobPawn != __instance?.pawn)
                return true;

            __result = true;
            return false;
        }

        [MpCompatPrefix("GiddyUp.MountUtility", "Dismount")]
        private static bool PreDismountForAbility(Pawn rider, Pawn animal)
        {
            return rider != preserveMountedRiderDuringAbility || animal != preserveMountedAnimalDuringAbility;
        }

        [MpCompatPostfix(WingedFlyerType, "MakeFlyer")]
        private static void PostWingedFlyerMakeFlyer(Pawn pawn, object __result)
        {
            TryAttachPendingFlyerRider(pawn, __result);
        }

        [MpCompatPostfix(typeof(PawnFlyer), nameof(PawnFlyer.MakeFlyer))]
        private static void PostPawnFlyerMakeFlyer(Pawn pawn, PawnFlyer __result)
        {
            if (__result?.GetType().FullName == DragonJumpFlyerType)
                TryAttachPendingFlyerRider(pawn, __result);
        }

        [MpCompatPrefix(WingedFlyerType, "RespawnPawn")]
        private static void PreWingedFlyerRespawn(object __instance, out List<Pawn> __state)
        {
            __state = DetachAdditionalHeldPawns(__instance);
        }

        [MpCompatFinalizer(WingedFlyerType, "RespawnPawn")]
        private static void PostWingedFlyerRespawn(object __instance, List<Pawn> __state)
        {
            RespawnDetachedPawns(__instance, __state);
        }

        [MpCompatPrefix(typeof(PawnFlyer), "RespawnPawn")]
        private static void PrePawnFlyerRespawn(PawnFlyer __instance, out List<Pawn> __state)
        {
            __state = __instance?.GetType().FullName == DragonJumpFlyerType
                ? DetachAdditionalHeldPawns(__instance)
                : null;
        }

        [MpCompatFinalizer(typeof(PawnFlyer), "RespawnPawn")]
        private static void PostPawnFlyerRespawn(PawnFlyer __instance, List<Pawn> __state)
        {
            if (__instance?.GetType().FullName == DragonJumpFlyerType)
                RespawnDetachedPawns(__instance, __state);
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

        private static bool ShouldPreserveMountedAnimalForAbility(Pawn_JobTracker jobTracker, Job newJob, out Pawn rider)
        {
            rider = null;

            if (jobTracker?.pawn == null || jobTracker.curJob?.def?.defName != MountedJobDefName)
                return false;

            rider = jobTracker.curJob.GetTarget(TargetIndex.A).Pawn;
            if (rider == null || !IsAbilityJob(newJob))
                return false;

            return true;
        }

        private static bool IsAbilityJob(Job job)
        {
            if (job == null)
                return false;

            return job.ability != null || job.verbToUse is Verb_CastAbility;
        }

        private static bool IsDragonFlyAbility(Job job)
        {
            var ability = job?.ability ?? (job?.verbToUse as Verb_CastAbility)?.ability;
            var abilityType = ability?.GetType().FullName;

            return abilityType == DragonJumpAbilityType || abilityType == WingedFlyerAbilityType;
        }

        private static void TryAttachPendingFlyerRider(Pawn animal, object flyer)
        {
            if (animal == null)
                return;

            if (!pendingFlyerRidersByAnimal.TryGetValue(animal, out var rider))
                return;

            pendingFlyerRidersByAnimal.Remove(animal);

            if (flyer is not IThingHolder holder)
                return;

            if (rider == null || rider.Destroyed)
                return;

            if (rider.Spawned)
                rider.DeSpawn();

            holder.GetDirectlyHeldThings().TryAddOrTransfer(rider, canMergeWithExistingStacks: true);
        }

        private static List<Pawn> DetachAdditionalHeldPawns(object flyer)
        {
            if (flyer is not IThingHolder holder)
                return null;

            var heldThings = holder.GetDirectlyHeldThings();
            List<Pawn> detachedPawns = null;
            Pawn primaryPawn = null;

            for (var i = 0; i < heldThings.Count; i++)
            {
                if (heldThings[i] is Pawn pawn)
                {
                    primaryPawn = pawn;
                    break;
                }
            }

            if (primaryPawn == null)
                return null;

            for (var i = heldThings.Count - 1; i >= 0; i--)
            {
                if (heldThings[i] is not Pawn pawn)
                    continue;

                if (pawn == primaryPawn)
                    continue;

                detachedPawns ??= [];
                detachedPawns.Add(pawn);
                heldThings.Remove(pawn);
            }

            detachedPawns?.Reverse();
            return detachedPawns;
        }

        private static void RespawnDetachedPawns(object flyer, List<Pawn> detachedPawns)
        {
            if (detachedPawns == null || detachedPawns.Count == 0 || flyer is not Thing thing || thing.Map == null)
                return;

            for (var i = 0; i < detachedPawns.Count; i++)
            {
                var pawn = detachedPawns[i];
                if (pawn == null || pawn.Destroyed)
                    continue;

                GenSpawn.Spawn(pawn, thing.Position, thing.Map);
            }
        }
    }
}