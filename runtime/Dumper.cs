using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HutongGames.PlayMaker;
using NWH.VehiclePhysics2;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Writes the vanilla contract: every vehicle and car part prefab the game has loaded, as Snapshot JSON
    /// (BepInEx/Apocars-vanilla/{vehicles,parts}/<name>.json), plus what the world around them is (world.json):
    /// layers and which collide, PlayMaker's global variables and events, the spawn lists cars and parts are in.
    internal static class Dumper
    {
        public static string Dir => Path.Combine(BepInEx.Paths.BepInExRootPath, "Apocars-vanilla");
        public static bool Running { get; private set; }

        public static IEnumerator Run()
        {
            if (Running) yield break;
            Running = true;
            int vehicles = 0, parts = 0, failed = 0;
            var started = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo("Vanilla dump: started.");
            try { Directory.CreateDirectory(Path.Combine(Dir, "vehicles")); Directory.CreateDirectory(Path.Combine(Dir, "parts")); }
            catch (Exception e) { Plugin.Log.LogError("Vanilla dump: " + e.Message); Running = false; yield break; }

            var targets = Targets();
            Plugin.Log.LogInfo($"Vanilla dump: {targets.Count(t => t.Value == "vehicles")} vehicles, {targets.Count(t => t.Value == "parts")} parts.");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in targets)
            {
                var go = kv.Key;
                if (go == null) continue;
                try
                {
                    var snap = new Snapshot(go.transform);
                    var doc = new Dictionary<string, object>
                    {
                        ["apocars"] = Plugin.Version,
                        ["game"] = Application.version,
                        ["kind"] = kv.Value.TrimEnd('s'),
                        ["name"] = go.name,
                        ["root"] = snap.Hierarchy(),
                        ["assets"] = snap.Assets,
                    };
                    if (snap.Problems.Count > 0) doc["problems"] = snap.Problems.Cast<object>().ToList();
                    string file = Safe(go.name);
                    for (int n = 2; !used.Add(kv.Value + "/" + file); n++) file = Safe(go.name) + "_" + n;
                    File.WriteAllText(Path.Combine(Dir, kv.Value, file + ".json"), Json.Write(doc));
                    if (kv.Value == "vehicles") vehicles++; else parts++;
                }
                catch (Exception e) { failed++; Plugin.Log.LogWarning($"Vanilla dump: '{go.name}' failed: {e}"); }
                yield return null; // one object per frame
            }

            try { File.WriteAllText(Path.Combine(Dir, "world.json"), Json.Write(World(targets))); }
            catch (Exception e) { failed++; Plugin.Log.LogWarning("Vanilla dump: world.json failed: " + e); }

            Plugin.Log.LogInfo($"Vanilla dump: {vehicles} vehicles, {parts} parts in {Time.realtimeSinceStartup - started:0.0} s" +
                               (failed > 0 ? $", {failed} failed (see above)" : "") + $" -> {Dir}");
            Running = false;
        }

        private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        /// Prefab roots (not in any scene) that are vehicles or car parts.
        private static List<KeyValuePair<GameObject, string>> Targets()
        {
            var list = new List<KeyValuePair<GameObject, string>>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.transform.parent != null || go.scene.IsValid()) continue;
                if ((go.hideFlags & HideFlags.DontSave) != 0) continue;
                string kind = null;
                if (go.GetComponent<VehicleController>() != null || Tag(go, "Vehicle")) kind = "vehicles";
                else if (Tag(go, "vehPart") || Tag(go, "vehPartRemoved") || HasFsm(go, "ID")) kind = "parts";
                if (kind != null) list.Add(new KeyValuePair<GameObject, string>(go, kind));
            }
            return list.OrderBy(k => k.Value).ThenBy(k => k.Key.name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool Tag(GameObject go, string tag) { try { return go.CompareTag(tag); } catch { return false; } }

        private static bool HasFsm(GameObject go, string name) => go.GetComponents<PlayMakerFSM>().Any(f => f.FsmName == name);

        // ------------------------------------------------------------------ the world around them

        private static Dictionary<string, object> World(List<KeyValuePair<GameObject, string>> targets)
        {
            var w = new Dictionary<string, object> { ["apocars"] = Plugin.Version, ["game"] = Application.version, ["unity"] = Application.unityVersion };

            var layers = new Dictionary<string, object>();
            for (int i = 0; i < 32; i++)
            {
                string n = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(n)) continue;
                var hits = new List<object>();
                for (int j = 0; j < 32; j++) if (!string.IsNullOrEmpty(LayerMask.LayerToName(j)) && !Physics.GetIgnoreLayerCollision(i, j)) hits.Add(j);
                layers[i.ToString()] = new Dictionary<string, object> { ["name"] = n, ["collides"] = hits };
            }
            w["layers"] = layers;

            // PlayMaker globals (UI text objects, the player, cameras...): what an FSM's {name} means
            try
            {
                var globals = PlayMakerGlobals.Instance;
                if (globals != null)
                {
                    var scratch = new GameObject("apocars-scratch");
                    try
                    {
                        var snap = new Snapshot(scratch.transform);
                        w["globals"] = snap.Fields(globals, globals.GetType(), 0);
                        w["globalAssets"] = snap.Assets;
                    }
                    finally { Object.Destroy(scratch); }
                }
            }
            catch (Exception e) { w["globals"] = "failed: " + e.Message; }

            // the lists cars and parts are spawned from (ArrayMaker lists holding prefabs)
            var lists = new List<object>();
            var known = new HashSet<GameObject>(targets.Select(t => t.Key));
            try
            {
                foreach (var proxy in Object.FindObjectsOfType<PlayMakerArrayListProxy>())
                {
                    var arr = proxy.arrayList;
                    if (arr == null) continue;
                    var items = new List<object>();
                    int hit = 0;
                    foreach (var e in arr)
                    {
                        var g = e as GameObject;
                        if (g != null && known.Contains(g)) hit++;
                        items.Add(g != null ? g.name : e?.ToString());
                    }
                    if (hit == 0) continue;
                    lists.Add(new Dictionary<string, object>
                    {
                        ["object"] = PathOf(proxy.transform),
                        ["scene"] = proxy.gameObject.scene.name,
                        ["reference"] = proxy.referenceName,
                        ["items"] = items,
                    });
                }
            }
            catch (Exception e) { lists.Add("failed: " + e.Message); }
            w["spawnLists"] = lists;

            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i).name);
            w["scenes"] = scenes;
            return w;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
