using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ES3Internal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Where built vehicles and parts are kept as prefabs, and where copies of them come from.
    /// Templates sit switched on under a switched-off holder: asleep there, and every copy (ours, a spawner's,
    /// Easy Save's when it loads a game) comes out switched on, as the game's own prefabs do.
    internal static class Garage
    {
        private static Transform _holder;
        public static readonly Dictionary<string, GameObject> Templates = new Dictionary<string, GameObject>();

        public static Transform Holder
        {
            get
            {
                if (_holder != null) return _holder;
                var go = new GameObject("Apocars templates");
                go.SetActive(false);
                Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _holder = go.transform;
                return _holder;
            }
        }

        /// A stable Easy Save prefab id for one of our templates (the same every session, so saves find it).
        public static long PrefabId(string key)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in Plugin.PrefabSalt + "/" + key) { h ^= c; h *= 1099511628211UL; }
                return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
            }
        }

        /// Gives a template its own Easy Save identity and registers it, so saved copies load back as it.
        public static void Register(string key, GameObject template)
        {
            var es3 = template.GetComponent<ES3Prefab>() ?? template.AddComponent<ES3Prefab>();
            es3.prefabId = PrefabId(key);
            es3.localRefs = new ES3RefIdDictionary();
            Templates[key] = template;
            AddToManagers(es3);
        }

        /// A template being replaced leaves the Easy Save managers (its successor takes its id).
        public static void Unregister(GameObject template)
        {
            var es3 = template != null ? template.GetComponent<ES3Prefab>() : null;
            if (es3 == null) return;
            foreach (var mgr in Object.FindObjectsOfType<ES3ReferenceMgrBase>()) mgr.prefabs.Remove(es3);
            foreach (var kv in new List<KeyValuePair<string, GameObject>>(Templates)) if (kv.Value == template) Templates.Remove(kv.Key);
        }

        public static void ReplaceInCarList(GameObject old, GameObject copy)
        {
            foreach (var proxy in Object.FindObjectsOfType<PlayMakerArrayListProxy>())
            {
                if (proxy.referenceName != "ArrayList_Cars" && proxy.name != "ArrayList_Cars") continue;
                int i = proxy.arrayList != null ? proxy.arrayList.IndexOf(old) : -1;
                if (i >= 0) proxy.arrayList[i] = copy;
                else if (proxy.arrayList != null && !proxy.arrayList.Contains(copy)) proxy.arrayList.Add(copy);
                return;
            }
        }

        public static void AddToManagers(ES3Prefab es3)
        {
            foreach (var mgr in Object.FindObjectsOfType<ES3ReferenceMgrBase>())
                try { mgr.AddPrefab(es3); } catch (Exception e) { Plugin.Log.LogWarning("Easy Save manager: " + e.Message); }
        }

        /// Scene loads bring new Easy Save managers: every template goes into them too.
        public static void Reregister()
        {
            foreach (var t in Templates.Values)
                if (t != null && t.GetComponent<ES3Prefab>() is ES3Prefab es3) AddToManagers(es3);
        }

        /// A copy of a template in front of the player, on the ground.
        public static GameObject SpawnInFront(GameObject template, float distance = 6f)
        {
            var player = GameObject.FindWithTag("Player");
            Transform from = player != null ? player.transform : Camera.main != null ? Camera.main.transform : null;
            Vector3 fwd = from != null ? Vector3.ProjectOnPlane(from.forward, Vector3.up).normalized : Vector3.forward;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            Vector3 pos = (from != null ? from.position : Vector3.zero) + fwd * distance;
            if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore)) pos = hit.point + Vector3.up * 1.0f;
            var rot = Quaternion.LookRotation(Vector3.Cross(fwd, Vector3.up), Vector3.up); // broadside to the player
            var copy = Object.Instantiate(template, pos, rot);
            copy.name = template.name + "(Clone)";
            AddToCarList(copy);
            return copy;
        }

        /// Spawned cars go into the game's car list, as its spawners put theirs (that's what gets saved).
        public static void AddToCarList(GameObject car)
        {
            foreach (var proxy in Object.FindObjectsOfType<PlayMakerArrayListProxy>())
            {
                if (proxy.referenceName != "ArrayList_Cars" && proxy.name != "ArrayList_Cars") continue;
                if (proxy.arrayList != null && !proxy.arrayList.Contains(car)) proxy.arrayList.Add(car);
                return;
            }
        }

        // ------------------------------------------------------------------ test: a vanilla car rebuilt from its dump

        public static void RebuildVanilla(string name)
        {
            string file = Path.Combine(Dumper.Dir, "vehicles", name + ".json");
            if (!File.Exists(file)) { Plugin.Log.LogWarning($"Rebuild: no {file} (run Tools.DumpVanilla first)."); return; }
            var started = Time.realtimeSinceStartup;
            var doc = (Dictionary<string, object>)Json.Read(File.ReadAllText(file));
            Assets.Forget();
            Assets.Missing.Clear();
            var rb = new Rebuild();
            var src = rb.Load(name, doc);
            var root = rb.Create(src, Json.Obj(doc, "root"), Holder);
            rb.Apply();
            root.name = name + "_apocars";
            Register("rebuilt/" + name, root);
            var copy = SpawnInFront(root);
            float ms = (Time.realtimeSinceStartup - started) * 1000f;

            var report = new List<string> { $"Rebuilt '{name}' from {file} in {ms:0} ms; spawned at {copy.transform.position}." };
            report.Add($"{rb.Problems.Count} problem(s):");
            report.AddRange(rb.Problems);
            report.Add($"{Assets.Missing.Count} asset(s) not found:");
            report.AddRange(Assets.Missing.Distinct());
            string log = Path.Combine(BepInEx.Paths.BepInExRootPath, $"Apocars-rebuild-{name}.txt");
            File.WriteAllLines(log, report);
            Plugin.Log.LogInfo($"Rebuild: '{name}' built in {ms:0} ms and spawned in front of you; {rb.Problems.Count} problem(s), " +
                               $"{Assets.Missing.Distinct().Count()} missing asset(s) -> {log}");
        }
    }
}
