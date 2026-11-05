using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HutongGames.PlayMaker;
using NWH.VehiclePhysics2;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// The game's own vehicles, parts and materials, as Apocars builds from them. A vehicle or part is read live from the
    /// game's prefab (so it always matches the installed version of the game) and kept as a snapshot document for the
    /// session; without the prefab loaded, the vanilla dump in BepInEx/Apocars-vanilla stands in.
    internal static class Vanilla
    {
        private static readonly Dictionary<string, Dictionary<string, object>> Docs = new Dictionary<string, Dictionary<string, object>>();
        private static Dictionary<string, GameObject> _prefabs;
        private static Dictionary<string, Material> _materials;

        /// Forgets what was found (a new scene can bring other prefabs).
        public static void Forget() { _prefabs = null; _materials = null; Docs.Clear(); Assets.Forget(); }

        public static Dictionary<string, GameObject> Prefabs
        {
            get
            {
                if (_prefabs != null) return _prefabs;
                _prefabs = new Dictionary<string, GameObject>();
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || go.transform.parent != null || go.scene.IsValid() || (go.hideFlags & HideFlags.DontSave) != 0) continue;
                    if (go.GetComponent<VehicleController>() == null && !go.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == "ID")) continue;
                    if (!_prefabs.ContainsKey(go.name)) _prefabs[go.name] = go;
                }
                return _prefabs;
            }
        }

        /// True once the game's vehicles are loaded (in a game, not the main menu).
        public static bool Ready => Prefabs.ContainsKey("Poloska");

        /// A vanilla vehicle or part as a snapshot document ({ root, assets }), or null.
        public static Dictionary<string, object> Doc(string name)
        {
            if (Docs.TryGetValue(name, out var d)) return d;
            if (Prefabs.TryGetValue(name, out var prefab))
            {
                var snap = new Snapshot(prefab.transform);
                d = new Dictionary<string, object> { ["name"] = name, ["root"] = snap.Hierarchy(), ["assets"] = snap.Assets };
            }
            else
            {
                foreach (var sub in new[] { "vehicles", "parts" })
                {
                    string file = Path.Combine(Dumper.Dir, sub, name + ".json");
                    if (File.Exists(file)) { d = (Dictionary<string, object>)Json.Read(File.ReadAllText(file)); break; }
                }
            }
            if (d != null) Docs[name] = d;
            return d;
        }

        /// A game material by name, or null.
        private static bool Textured(Material m)
        {
            try { return m.HasProperty("_MainTex") && m.mainTexture != null; } catch { return false; }
        }

        public static Material Material(string name)
        {
            if (_materials == null)
            {
                _materials = new Dictionary<string, Material>();
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || Gltf.Ours.Contains(m)) continue;          // ours, from a model
                    // of several with one name, the one with a texture (a texture-less copy isn't the game's look)
                    if (_materials.TryGetValue(m.name, out var had) && Textured(had) || (had != null && !Textured(m))) continue;
                    _materials[m.name] = m;
                }
            }
            return _materials.TryGetValue(name, out var mat) ? mat : null;
        }
    }
}
