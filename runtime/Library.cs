using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ES3Internal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// The assets Apocars builds: every <id>.lua (with its .glb) in a mod's "apocars" folder (BepInEx/plugins/<mod>/apocars),
    /// or, for a standalone mod Apocars was built into, its own folder only. Built when a game's vehicles are loaded
    /// (before the game loads its save, so saved copies find them), rebuilt and respawned when their files change.
    internal static class Library
    {
        public const string FolderName = "apocars";

        public sealed class Asset
        {
            public string Id, Mod, Lua, Glb, Kind;
            public DateTime Stamp;
            public GameObject Template;
            public Gltf Model;
            public List<string> Problems = new List<string>();
            public string Error;
        }

        public static readonly Dictionary<string, Asset> All = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        private static bool _built;
        /// Set by a standalone build: only this folder is read.
        public static string OnlyFolder;

        public static IEnumerable<string> Folders()
        {
            if (OnlyFolder != null) { if (Directory.Exists(OnlyFolder)) yield return OnlyFolder; yield break; }
            if (!Directory.Exists(BepInEx.Paths.PluginPath)) yield break;
            foreach (var mod in Directory.GetDirectories(BepInEx.Paths.PluginPath))
            {
                if (Standalone.ClaimedBySomeoneElse(mod)) continue; // a standalone Apocars mod reads its own
                string d = Path.Combine(mod, FolderName);
                if (Directory.Exists(d)) yield return d;
            }
        }

        public static void Scan()
        {
            foreach (var dir in Folders())
                foreach (var lua in Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(Path.GetDirectoryName(lua)) == "lib") continue; // shared Lua, not an asset
                    string id = Path.GetFileNameWithoutExtension(lua);
                    if (All.TryGetValue(id, out var have) && !string.Equals(have.Lua, lua, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.LogWarning($"Two assets named '{id}': {have.Lua} and {lua}; the first is kept.");
                        continue;
                    }
                    if (have == null) All[id] = new Asset { Id = id, Lua = lua, Mod = Path.GetFileName(Path.GetDirectoryName(dir)) };
                }
        }

        /// In a game with the vanilla vehicles loaded: build everything not built yet.
        public static void BuildAll()
        {
            if (!Vanilla.Ready) return;
            Scan();
            foreach (var a in All.Values.ToList()) if (a.Template == null && a.Error == null) Build(a, respawn: false);
            if (!_built)
            {
                _built = true;
                int ok = All.Values.Count(a => a.Template != null);
                Plugin.Log.LogInfo($"Assets: {ok} built" + (All.Count > ok ? $", {All.Count - ok} failed (see above)" : "") + ".");
            }
        }

        private static DateTime StampOf(Asset a)
        {
            var t = File.Exists(a.Lua) ? File.GetLastWriteTimeUtc(a.Lua) : DateTime.MinValue;
            if (a.Glb != null && File.Exists(a.Glb)) { var g = File.GetLastWriteTimeUtc(a.Glb); if (g > t) t = g; }
            return t;
        }

        public static void Build(Asset a, bool respawn)
        {
            var started = Time.realtimeSinceStartup;
            a.Stamp = StampOf(a);
            a.Error = null;
            Dictionary<string, object> spec;
            Gltf glb;
            try
            {
                spec = Spec.Load(a.Lua);
                a.Kind = Json.Str(spec, "kind", "vehicle");
                string model = Json.Str(spec, "model", a.Id + ".glb");
                a.Glb = Path.Combine(Path.GetDirectoryName(a.Lua), model);
                if (!File.Exists(a.Glb)) throw new FileNotFoundException("no model " + model);
                glb = Gltf.Load(a.Glb);
                a.Stamp = StampOf(a);
            }
            catch (Exception e)
            {
                a.Error = e.Message;
                Plugin.Log.LogError($"Asset '{a.Id}': {e.Message} (the previous version stays)");
                return;
            }

            Assembler.Result res;
            try
            {
                if (a.Kind == "part") res = Parts.Build(a.Id, spec, glb, Garage.Holder);
                else res = Assembler.Build(a.Id, spec, glb, Garage.Holder);
            }
            catch (Exception e)
            {
                glb.Unload();
                a.Error = e.Message;
                Plugin.Log.LogError($"Asset '{a.Id}': build failed: {e}");
                return;
            }
            if (res.Root == null)
            {
                glb.Unload();
                a.Error = string.Join("; ", res.Problems);
                Plugin.Log.LogError($"Asset '{a.Id}': not built: {a.Error}");
                return;
            }

            var old = a.Template;
            var oldModel = a.Model;
            res.Root.name = a.Id;
            a.Template = res.Root;
            a.Model = glb;
            a.Problems = res.Problems.Concat(glb.Problems).Distinct().ToList();
            long id = Garage.PrefabId((a.Kind == "part" ? "part/" : "vehicle/") + a.Id);
            if (old != null) Garage.Unregister(old);
            Garage.Register((a.Kind == "part" ? "part/" : "vehicle/") + a.Id, res.Root);
            if (a.Kind == "part") Parts.Registered(a, res.Root);   // the game's item list: saving, selling, world loot
            Spawning.Registered(a, spec, res.Root);                 // the spawn lists the asset asked for

            int swapped = 0, waiting = 0;
            if (respawn) Respawn(id, res.Root, ref swapped, ref waiting);
            if (old != null) Object.Destroy(old);
            if (oldModel != null && respawn && waiting == 0) oldModel.Unload();

            float ms = (Time.realtimeSinceStartup - started) * 1000f;
            Report(a, ms);
            Plugin.Log.LogInfo($"Asset '{a.Id}' ({a.Kind}) built in {ms:0} ms" + (a.Problems.Count > 0 ? $", {a.Problems.Count} problem(s) -> BepInEx/Apocars-{a.Id}.txt" : "") +
                               (respawn ? $"; {swapped} copy(ies) respawned" + (waiting > 0 ? $", {waiting} waiting for you to get out" : "") : "") + ".");
        }

        private static void Report(Asset a, float ms)
        {
            try
            {
                var lines = new List<string> { $"{a.Id} ({a.Kind}) from {a.Lua}, built in {ms:0} ms.", $"{a.Problems.Count} problem(s):" };
                lines.AddRange(a.Problems);
                File.WriteAllLines(Path.Combine(BepInEx.Paths.BepInExRootPath, $"Apocars-{a.Id}.txt"), lines);
            }
            catch { }
        }

        // ------------------------------------------------------------------ hot export

        private static readonly List<KeyValuePair<long, GameObject>> Pending = new List<KeyValuePair<long, GameObject>>();

        /// Copies of a rebuilt asset in the world are replaced by new ones where they are (one with the player in it waits).
        private static void Respawn(long prefabId, GameObject template, ref int swapped, ref int waiting)
        {
            foreach (var es3 in Object.FindObjectsOfType<ES3Prefab>())
            {
                if (es3.prefabId != prefabId || es3.gameObject == template) continue;
                var go = es3.gameObject;
                if (HasPlayer(go)) { waiting++; Pending.Add(new KeyValuePair<long, GameObject>(prefabId, go)); continue; }
                Swap(go, template);
                swapped++;
            }
        }

        private static bool HasPlayer(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) if (t.CompareTag("Player")) return true;
            return false;
        }

        private static void Swap(GameObject old, GameObject template)
        {
            var t = old.transform;
            var body = old.GetComponent<Rigidbody>();
            var copy = Object.Instantiate(template, t.position, t.rotation);
            copy.name = template.name + "(Clone)";
            var nb = copy.GetComponent<Rigidbody>();
            if (body != null && nb != null) { nb.velocity = body.velocity; nb.angularVelocity = body.angularVelocity; }
            // the fitted parts move over to the new copy (same hinge names), so a rebuilt car keeps its engine and wheels
            foreach (Transform hinge in t)
            {
                var nh = copy.transform.Find(hinge.name);
                if (nh == null) continue;
                var fitted = new List<Transform>();
                foreach (Transform c in hinge) if (c.CompareTag("vehPart")) fitted.Add(c);
                foreach (var c in fitted) c.SetParent(nh, false);
            }
            Garage.ReplaceInCarList(old, copy);
            Object.Destroy(old);
        }

        private static float _nextCheck;

        public static void Tick()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 1f;
            if (!Plugin.HotExport.Value || !_built) return;

            // new assets saved while the game runs
            int before = All.Count;
            Scan();
            if (All.Count != before) foreach (var a in All.Values.Where(x => x.Template == null && x.Error == null).ToList()) Build(a, respawn: false);

            foreach (var a in All.Values.ToList())
                if (StampOf(a) > a.Stamp) Build(a, respawn: true);

            // copies that had the player in them, once they got out
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                var p = Pending[i];
                if (p.Value == null) { Pending.RemoveAt(i); continue; }
                if (HasPlayer(p.Value)) continue;
                var asset = All.Values.FirstOrDefault(x => x.Template != null && x.Template.GetComponent<ES3Prefab>()?.prefabId == p.Key);
                if (asset != null) Swap(p.Value, asset.Template);
                Pending.RemoveAt(i);
            }
        }

        public static Asset Find(string id) => All.TryGetValue(id ?? "", out var a) ? a : null;
    }
}
