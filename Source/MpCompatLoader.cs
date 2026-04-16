using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Verse;

namespace Multiplayer.Compat
{
    public static class MpCompatLoader
    {
        static string NormalizeModId(string modId)
            => modId.NoModIdSuffix().ToLowerInvariant();

        static string FormatCompatIds(string[] modIds)
            => string.Join(", ", modIds.Select(modId => $"{modId} -> {NormalizeModId(modId)}"));

        internal static void Load(ModContentPack content)
        {
            LoadConditional(content);

            foreach (var asm in content.assemblies.loadedAssemblies)
                InitCompatInAsm(asm);

            ClearCaches();
        }
        
        static void LoadConditional(ModContentPack content)
        {
            var asmPath = ModContentPack
                .GetAllFilesForModPreserveOrder(content, "Referenced/", f => f.ToLower() == ".dll")
                .FirstOrDefault(f => f.Item2.Name == "Multiplayer_Compat_Referenced.dll")?.Item2;

            if (asmPath == null)
            {
                return;
            }

            var asm = AssemblyDefinition.ReadAssembly(asmPath.FullName);

            foreach (var t in asm.MainModule.GetTypes().ToArray())
            {
                var attr = t.CustomAttributes
                    .Where(a => a.Constructor.DeclaringType.Name is nameof(MpCompatForAttribute) or nameof(MpCompatRequireModAttribute))
                    .ToArray();
                if (!attr.Any()) continue;

                var compatIds = attr
                    .Select(a => (string)a.ConstructorArguments.First().Value)
                    .ToArray();

                Log.Message($"MPCompat :: Conditional compat discovered {t.FullName} ids [{FormatCompatIds(compatIds)}]");

                var matchedMods = attr.Select(a => (string)a.ConstructorArguments.First().Value)
                    .Select(modId => LoadedModManager.RunningMods.FirstOrDefault(m => NormalizeModId(m.PackageId) == NormalizeModId(modId)))
                    .Where(mod => mod != null)
                    .ToArray();

                var anyMod = matchedMods.Any();

                if (anyMod)
                {
                    Log.Message($"MPCompat :: Conditional compat keep {t.FullName} matches [{string.Join(", ", matchedMods.Select(m => m.PackageId))}]");
                }
                else
                {
                    Log.Message($"MPCompat :: Conditional compat remove {t.FullName} (no installed mod match)");
                }

                if (!anyMod)
                    asm.MainModule.Types.Remove(t);
            }

            var stream = new MemoryStream();
            asm.Write(stream);

            var loadedAsm = AppDomain.CurrentDomain.Load(stream.ToArray());
            content.assemblies.loadedAssemblies.Add(loadedAsm);
        }

        static void InitCompatInAsm(Assembly asm)
        {
            var compatEntries = asm.GetTypes()
                .Where(t => t.HasAttribute<MpCompatForAttribute>())
                .SelectMany(
                    t => (MpCompatForAttribute[]) t.GetCustomAttributes(typeof(MpCompatForAttribute), false),
                    (type, compat) => new { type, compat }
                )
                .ToArray();

            foreach (var entry in compatEntries)
                Log.Message($"MPCompat :: Compat discovered {entry.type.FullName} in {asm.GetName().Name} for {entry.compat.PackageId} -> {NormalizeModId(entry.compat.PackageId)}");

            var queue = compatEntries
                .Join(LoadedModManager.RunningMods,
                    box => NormalizeModId(box.compat.PackageId),
                    mod => NormalizeModId(mod.PackageId),
                    (box, mod) => new { box.type, box.compat, mod })
                .ToArray();

            foreach (var entry in compatEntries.Where(entry => !queue.Any(match => match.type == entry.type && match.compat == entry.compat)))
                Log.Message($"MPCompat :: Compat no match {entry.type.FullName} for {entry.compat.PackageId} -> {NormalizeModId(entry.compat.PackageId)}");

            foreach (var action in queue) 
            {
                try {
                    Log.Message($"MPCompat :: Compat match {action.type.FullName} <- {action.mod.PackageId}");
                    Activator.CreateInstance(action.type, action.mod);
                    Log.Message($"MPCompat :: Compat init ok {action.type.FullName} for {action.mod.PackageId}");
                } catch(Exception e) {
                    Log.Error($"MPCompat :: Exception loading {action.type.FullName} for {action.mod.PackageId}: {e.InnerException ?? e}");
                }
            }
        }

        static void ClearCaches()
        {
            // Clear the GenTypes cache first, as MP will use it to create its own cache (built through GenTypes.AllTypes call if null)
            GenTypes.ClearCache();

            // As we're adding the new assembly, the classes added by it aren't included by the MP GenTypes AllSubclasses/AllSubclassesNonAbstract optimization
            // GenTypes.ClearCache() on its own won't work, as MP isn't doing anything when it's called.
            var mpType = AccessTools.TypeByName("Multiplayer.Client.Util.TypeCache") ?? AccessTools.TypeByName("Multiplayer.Client.Multiplayer");
            ((IDictionary)AccessTools.Field(mpType, "subClasses").GetValue(null)).Clear();
            ((IDictionary)AccessTools.Field(mpType, "subClassesOrdered").GetValue(null)).Clear();
            ((IDictionary)AccessTools.Field(mpType, "subClassesNonAbstract").GetValue(null)).Clear();
            ((IDictionary)AccessTools.Field(mpType, "interfaceImplementations").GetValue(null)).Clear();
            ((IDictionary)AccessTools.Field(mpType, "interfaceImplementationsOrdered").GetValue(null)).Clear();
            AccessTools.Method(mpType, "CacheTypeHierarchy").Invoke(null, []);

            // Clear/re-init the list of ISyncSimple implementations and Session subclasses.
            AccessTools.Method("Multiplayer.Client.ApiSerialization:Init").Invoke(null, []);
            // Clear/re-init the localDefInfos dictionary so it contains the classes added from referenced assembly.
            AccessTools.Method("Multiplayer.Client.MultiplayerData:CollectDefInfos").Invoke(null, []);
        }
    }
}