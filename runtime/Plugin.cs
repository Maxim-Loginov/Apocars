using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace Apocars
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(ApocasetterGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
#if STANDALONE
        // filled in by the Blender add-on's Build (same lengths): QQQQ... a hash of the mod's name, NNNN... its name
        public const string Guid = "com.apocalypter.apocars.mod.QQQQQQQQQQQQQQQQ";
        public const string Name = "NNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNN";
        public static string DisplayName => Name.TrimEnd(' ');
#else
        public const string Guid = "com.apocalypter.apocars";
        public const string Name = "Apocars";
        public static string DisplayName => Name;
#endif
        /// What Easy Save prefab ids are made from: the same in the lib and every mod built from it, so a car saved with
        /// one loads with the other.
        public const string PrefabSalt = "com.apocalypter.apocars";
        public const string Version = "0.7.6";
        private const string ApocasetterGuid = "com.denis.apocalypter.apocasetter";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> DumpVanilla;
        internal static ConfigEntry<bool> VerboseLog;
        internal static ConfigEntry<string> RebuildVanilla;
        internal static ConfigEntry<string> SpawnAsset;
        internal static ConfigEntry<bool> HotExport;
        internal static Runner Host;

        private void Awake()
        {
            Log = Logger;
#if STANDALONE
            Library.OnlyFolder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Info.Location) ?? "", Library.FolderName);
#endif
            Config.Bind("General", "Apocasetter", true, "Show in the Apocasetter Mods menu.");
            VerboseLog = Config.Bind("General", "VerboseLog", false, "Extra detail in the log.");
            DumpVanilla = Config.Bind("Tools", "DumpVanilla", false,
                "In a loaded game: writes every vanilla vehicle and car part, with all their components and FSMs, to BepInEx/Apocars-vanilla. Switches itself off when done.");

            RebuildVanilla = Config.Bind("Tools", "RebuildVanilla", "",
                "Test: the name of a dumped vanilla vehicle (Poloska, Junker...): it is rebuilt from scratch out of its dump and spawned in front of you. Clears itself.");
            SpawnAsset = Config.Bind("Tools", "Spawn", "",
                "The id of an Apocars vehicle or part: a copy is spawned in front of you. Clears itself.");
            HotExport = Config.Bind("General", "HotExport", true,
                "Rebuild an asset when its .lua or .glb changes (the Blender add-on's Hot Export) and respawn its copies in place.");
            // built as soon as a game's vehicles are loaded: before the game loads its save, which may hold copies of them
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) =>
            {
                Vanilla.Forget();
                try { Library.BuildAll(); } catch (Exception e) { Log.LogError("Building assets failed: " + e); }
                Garage.Reregister();
            };

            try { Spawning.Patch(new HarmonyLib.Harmony(Info.Metadata.GUID)); } catch (Exception e) { Log.LogError("Spawn lists: " + e); }

            // the game destroys plugin objects on scene loads: per-frame work lives on our own object
            var go = new GameObject("Apocars");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            Host = go.AddComponent<Runner>();
#if STANDALONE
            Log.LogInfo($"{DisplayName}: built with Apocars {Version}, reads {Library.OnlyFolder}.");
#else
            Log.LogInfo($"{Name} {Version} loaded.");
#endif
        }

        internal class Runner : MonoBehaviour
        {
            private float _next;

            private void Update()
            {
                try { Library.Tick(); } catch (Exception e) { Log.LogError("Hot export: " + e); }
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 0.5f;
                if (!string.IsNullOrEmpty(RebuildVanilla.Value))
                {
                    string name = RebuildVanilla.Value.Trim();
                    RebuildVanilla.Value = "";
                    try { Garage.RebuildVanilla(name); } catch (Exception e) { Log.LogError("Rebuild failed: " + e); }
                }
                if (!string.IsNullOrEmpty(SpawnAsset.Value))
                {
                    string id = SpawnAsset.Value.Trim();
                    SpawnAsset.Value = "";
                    var a = Library.Find(id);
                    if (a?.Template == null) Log.LogWarning($"Spawn: no built asset '{id}'" + (a?.Error != null ? $" ({a.Error})" : ""));
                    else { var copy = Garage.SpawnInFront(a.Template); Log.LogInfo($"Spawned '{id}' at {copy.transform.position}."); }
                }
                if (DumpVanilla.Value && !Dumper.Running)
                {
                    DumpVanilla.Value = false;
                    StartCoroutine(Dumper.Run());
                }
            }
        }
    }
}
