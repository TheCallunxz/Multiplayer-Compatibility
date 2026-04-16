using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Dragon's Descent by Onyxae</summary>
    /// <see href="https://github.com/Aether-Guild/Dragons-Descent/"/>
    /// <see href="https://steamcommunity.com/workshop/filedetails/?id=2026992161"/>
    [MpCompatFor("onyxae.dragonsdescent")]
    public class DragonsDescent
    {
        //// Dragon Abilities (VEF) ////
        // Dragon abilities (Ability_DragonJump, Ability_WingedFlyer, fire breath, etc.)
        // extend VEF.Abilities.Ability but are granted via pawn.abilities.GainAbility(),
        // so they live in Pawn_AbilityTracker, NOT in VEF's CompAbilities.
        //
        // MP serialises the `this` pointer of VEF.StartAbilityJob using the *declared*
        // registration type (VEF.Abilities.Ability), so per-subtype explicit workers are
        // never consulted — the lookup always uses VEF.Abilities.Ability as the key.
        //
        // Fix: register ONE explicit (non-implicit) sync worker directly for
        // VEF.Abilities.Ability.  Explicit entries are checked before implicit entries in
        // SyncWorkerDictionaryTree.TryGetValue, so this worker wins over VEF's implicit
        // worker and handles both the normal CompAbilities path AND the pawn.abilities
        // fallback needed for DD dragon abilities.
        private static AccessTools.FieldRef<object, Pawn> vefAbilityPawnField;
        private static AccessTools.FieldRef<object, Thing> vefAbilityHolderField;
        private static FastInvokeHandler vefAbilityInitMethod;

        // VEF CompAbilities / CompAbilitiesApparel — needed to handle non-DD VEF abilities
        private static Type vefCompAbilitiesType;
        private static Type vefCompAbilitiesApparelType;
        private static AccessTools.FieldRef<object, IEnumerable> vefLearnedAbilitiesField;
        private static AccessTools.FieldRef<object, IEnumerable> vefGivenAbilitiesField;
        private static FastInvokeHandler vefAbilityApparelPawnGetter;

        //// Altar and Rituals ////
        // Command_RitualEffect
        private static ConstructorInfo ritualEffectCommandCtor;
        private static AccessTools.FieldRef<object, Thing> ritualEffectCommandSourceField;
        private static AccessTools.FieldRef<object, object> ritualEffectCommandRitualField;
        private static AccessTools.FieldRef<object, object> ritualEffectCommandRitualRequestField;
        private static FastInvokeHandler ritualEffectCommandCreateSetupMethod;

        // MapComponent_Tracker
        private static Type mapComponentTrackerType;
        private static AccessTools.FieldRef<object, object> mapComponentTrackerRitualsField;

        // Ritual
        private static AccessTools.FieldRef<object, Def> ritualDefField;

        // RitualActivator
        private static FastInvokeHandler ritualActivatorInitializeMethod;

        // RitualTracker
        private static AccessTools.FieldRef<object, Map> ritualTrackerMapField;

        public DragonsDescent(ModContentPack mod)
        {
            // Incubator
            {
                // (Toggle) accelerate growth, and dev commands: reset progress, force tick, hatch now
                MpCompat.RegisterLambdaMethod("DD.CompEggIncubator", "CompGetGizmosExtra", 1, 2, 3, 4)
                    .Skip(1)
                    .SetDebugOnly();

                // Place on ground
                MpCompat.RegisterLambdaDelegate("DD.CompProperties_EggIncubator", "CreateGizmo", 0);
            }

            // AbilityComp_AbilityControl seems unused, skipping this gizmo

            // Altar and Rituals (early setup — types are available at startup)
            {
                // MapComponent_Tracker
                mapComponentTrackerType = AccessTools.TypeByName("DD.MapComponent_Tracker");
                mapComponentTrackerRitualsField = AccessTools.FieldRefAccess<object>(mapComponentTrackerType, "rituals");

                // Ritual
                ritualDefField = AccessTools.FieldRefAccess<Def>("DD.Ritual:def");

                // RitualActivator
                ritualActivatorInitializeMethod = MethodInvoker.GetHandler(AccessTools.Method("DD.RitualActivator:Initialize"));

                // RitualTracker
                var ritualTrackerType = AccessTools.TypeByName("DD.RitualTracker");
                ritualTrackerMapField = AccessTools.FieldRefAccess<Map>(ritualTrackerType, "map");
                MP.RegisterSyncWorker<object>(SyncRitualTracker, ritualTrackerType);
            }

            LongEventHandler.ExecuteWhenFinished(LatePatch);
        }

        private static void LatePatch()
        {
            // Dragon Abilities — replace VEF's implicit sync worker with an explicit one.
            //
            // MP resolves sync workers using the *declared* type passed to RegisterSyncMethod,
            // which for VEF.StartAbilityJob is always "VEF.Abilities.Ability".
            // SyncWorkerDictionaryTree.TryGetValue checks explicitEntries (keyed by exact type)
            // before iterating implicitEntries, so an explicit registration for
            // VEF.Abilities.Ability wins over VEF's implicit one regardless of registration order.
            //
            // Our explicit worker replicates VEF's CompAbilities lookup for normal VEF abilities
            // and adds a pawn.abilities fallback for DD dragon abilities (which are granted via
            // pawn.abilities.GainAbility and are therefore absent from CompAbilities).
            var vefAbilityType = AccessTools.TypeByName("VEF.Abilities.Ability");
            if (vefAbilityType != null)
            {
                vefAbilityPawnField   = AccessTools.FieldRefAccess<Pawn>(vefAbilityType, "pawn");
                vefAbilityHolderField = AccessTools.FieldRefAccess<Thing>(vefAbilityType, "holder");
                vefAbilityInitMethod  = MethodInvoker.GetHandler(AccessTools.Method(vefAbilityType, "Init"));

                vefCompAbilitiesType = AccessTools.TypeByName("VEF.Abilities.CompAbilities");
                if (vefCompAbilitiesType != null)
                    vefLearnedAbilitiesField = AccessTools.FieldRefAccess<IEnumerable>(vefCompAbilitiesType, "learnedAbilities");

                vefCompAbilitiesApparelType = AccessTools.TypeByName("VEF.Abilities.CompAbilitiesApparel");
                if (vefCompAbilitiesApparelType != null)
                {
                    vefGivenAbilitiesField      = AccessTools.FieldRefAccess<IEnumerable>(vefCompAbilitiesApparelType, "givenAbilities");
                    vefAbilityApparelPawnGetter = MethodInvoker.GetHandler(AccessTools.PropertyGetter(vefCompAbilitiesApparelType, "Pawn"));
                }

                // Explicit (not implicit) — beats VEF's implicit worker in TryGetValue.
                MP.RegisterSyncWorker<ITargetingSource>(SyncDragonOrVEFAbility, vefAbilityType);
            }
            else
            {
                Log.Error("MPCompat :: DragonsDescent: Could not find VEF.Abilities.Ability — dragon ability sync workers will not be registered, expect desyncs on dragon ability use.");
            }

            // Altar and Rituals (late setup — Command_RitualEffect needs types fully loaded)
            {
                var ritualEffectCommand = AccessTools.TypeByName("DD.Command_RitualEffect");
                var ritualTrackerType = AccessTools.TypeByName("DD.RitualTracker");
                var ritualDefType = AccessTools.TypeByName("DD.RitualDef");

                ritualEffectCommandCtor = AccessTools.DeclaredConstructor(ritualEffectCommand, new[] { typeof(Thing), ritualTrackerType, ritualDefType });
                ritualEffectCommandSourceField = AccessTools.FieldRefAccess<Thing>(ritualEffectCommand, "source");
                ritualEffectCommandRitualField = AccessTools.FieldRefAccess<object>(ritualEffectCommand, "ritual");
                ritualEffectCommandRitualRequestField = AccessTools.FieldRefAccess<object>(ritualEffectCommand, "ritualRequest");
                ritualEffectCommandCreateSetupMethod = MethodInvoker.GetHandler(AccessTools.PropertyGetter(ritualEffectCommand, "CreateSetup"));

                MP.RegisterSyncMethod(ritualEffectCommand, "ActivateOnNoTarget").SetPreInvoke(PreActivateRitual);
                MP.RegisterSyncMethod(ritualEffectCommand, "ActivateOnLocalTarget").SetPreInvoke(PreActivateRitual);
                MP.RegisterSyncMethod(ritualEffectCommand, "ActivateOnGlobalTarget").SetPreInvoke(PreActivateRitual);
                MP.RegisterSyncWorker<Command>(SyncRitualEffectCommand, ritualEffectCommand);
            }
        }

        /// <summary>
        /// Unified sync worker for VEF.Abilities.Ability registered as EXPLICIT so it beats
        /// VEF's implicit worker in SyncWorkerDictionaryTree.TryGetValue.
        ///
        /// Handles three cases on the read side:
        ///   1. Normal VEF abilities stored in CompAbilities (holder → learnedAbilities).
        ///   2. VEF apparel abilities stored in CompAbilitiesApparel (holder → givenAbilities).
        ///   3. DD dragon abilities stored in pawn.abilities (pawn → AllAbilitiesForReading
        ///      fallback used when CompAbilities lookup finds nothing).
        ///
        /// Uses def.defName as the stable identifier instead of verb UID; each pawn normally
        /// holds at most one ability instance per AbilityDef.
        /// </summary>
        private static void SyncDragonOrVEFAbility(SyncWorker sync, ref ITargetingSource source)
        {
            if (sync.isWriting)
            {
                // holder covers normal VEF abilities; pawn is the fallback for DD abilities
                // (where holder may be null or set to the pawn which has no CompAbilities).
                sync.Write(vefAbilityHolderField(source));
                sync.Write(vefAbilityPawnField(source));
                sync.Write(((Ability)source).def.defName);
            }
            else
            {
                var holder  = sync.Read<Thing>();
                var pawn    = sync.Read<Pawn>();
                var defName = sync.Read<string>();

                // ── Path 1 & 2: VEF-managed abilities (holder has a CompAbilities comp) ──
                if (holder is ThingWithComps twc)
                {
                    // CompAbilities (e.g. Vanilla Psycasts Expanded abilities)
                    if (vefCompAbilitiesType != null && vefLearnedAbilitiesField != null)
                    {
                        var comp = twc.AllComps.FirstOrDefault(c => vefCompAbilitiesType.IsInstanceOfType(c));
                        if (comp != null)
                        {
                            foreach (var o in vefLearnedAbilitiesField(comp))
                            {
                                if (o is Ability a && a.def.defName == defName && o is ITargetingSource its)
                                {
                                    source = its;
                                    return;
                                }
                            }
                        }
                    }

                    // CompAbilitiesApparel (ability granted by worn item)
                    if (vefCompAbilitiesApparelType != null && vefGivenAbilitiesField != null)
                    {
                        var apparelComp = twc.AllComps.FirstOrDefault(c => vefCompAbilitiesApparelType.IsInstanceOfType(c));
                        if (apparelComp != null)
                        {
                            foreach (var o in vefGivenAbilitiesField(apparelComp))
                            {
                                if (o is Ability a && a.def.defName == defName && o is ITargetingSource its)
                                {
                                    // Replicate VEF's apparel-ability initialization so the
                                    // ability knows its owning pawn on non-host clients.
                                    var wearingPawn = vefAbilityApparelPawnGetter?.Invoke(apparelComp, Array.Empty<object>()) as Pawn;
                                    vefAbilityPawnField(its) = wearingPawn;
                                    vefAbilityInitMethod?.Invoke(its, Array.Empty<object>());
                                    source = its;
                                    return;
                                }
                            }
                        }
                    }
                }

                // ── Path 3: DD dragon abilities live in pawn.abilities, not CompAbilities ──
                // Dragon abilities are granted via pawn.abilities.GainAbility() and therefore
                // absent from VEF's CompAbilities.  Search the vanilla ability tracker instead.
                if (pawn?.abilities != null)
                {
                    foreach (var ability in pawn.abilities.AllAbilitiesForReading)
                    {
                        if (ability is ITargetingSource its && ability.def.defName == defName)
                        {
                            source = its;
                            return;
                        }
                    }
                }

                Log.Error($"MPCompat :: DragonsDescent: Could not find ability '{defName}' (holder={holder}, pawn={pawn}). Desync is likely on this client.");
            }
        }

        private static void SyncRitualTracker(SyncWorker sync, ref object tracker)
        {
            if (sync.isWriting)
                sync.Write(ritualTrackerMapField(tracker));
            else
            {
                var map = sync.Read<Map>();
                var comp = map.GetComponent(mapComponentTrackerType);
                tracker = mapComponentTrackerRitualsField(comp);
            }
        }

        private static void PreActivateRitual(object instance, object[] _)
        {
            // Create the request
            var ritualRequest = ritualEffectCommandCreateSetupMethod(instance);
            // Get the source
            var source = ritualEffectCommandSourceField(instance);
            // Initialize the request
            ritualActivatorInitializeMethod(ritualRequest, source);
            // Set the field inside of the instance to the request we created
            ritualEffectCommandRitualRequestField(instance) = ritualRequest;
        }

        private static void SyncRitualEffectCommand(SyncWorker sync, ref Command command)
        {
            if (sync.isWriting)
            {
                sync.Write(ritualEffectCommandSourceField(command));
                sync.Write(Find.CurrentMap.GetComponent(mapComponentTrackerType));

                var ritual = ritualEffectCommandRitualField(command);
                sync.Write(ritualDefField(ritual));
            }
            else
            {
                var source = sync.Read<Thing>();
                var mapComponent = sync.Read<MapComponent>();
                var tracker = mapComponentTrackerRitualsField(mapComponent);
                var def = sync.Read<Def>();

                command = (Command)ritualEffectCommandCtor.Invoke(new object[] { source, tracker, def });
            }
        }
    }
}