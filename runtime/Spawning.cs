using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Where built vehicles and parts turn up in the world: the game's spawn lists (ArrayMaker lists of prefabs its
    /// spawners pick from at random). Each list is known by its reference name and its spawner's object name; a list
    /// an asset asked for gets the asset's template added, as many times as its weight, whenever it fills itself
    /// (lists in map tiles, camps and buildings fill when those load) and at once for lists already there.
    internal static class Spawning
    {
        /// The spawn lists, as the game has them (keep in step with the Blender add-on's game.SPAWN_TABLES).
        private static readonly Dictionary<string, (string reference, string[] spawners)> Tables = new Dictionary<string, (string, string[])>
        {
            // vehicles
            ["car_camps"] = ("CarSpawn", new[] { "CarSpawn" }),                     // camps and buildings: Junker, Outrider, PigPen, Poloska...
            ["car_all"] = ("CarSpawn", new[] { "CarSpawn_All" }),                   // bigger camps and buildings: those and the Rust trucks
            ["car_starter"] = ("CarSpawn", new[] { "CarSpawn_StarterBase" }),       // the starter base
            ["car_unique"] = ("CarSpawn", new[] { "CarSpawn_Unique" }),             // one-car spots (Duke, Rustallion, a Poloska)
            ["trailer"] = ("CarSpawn", new[] { "TrailerSpawn" }),                   // trailers
            ["trailer_all"] = ("CarSpawn", new[] { "TrailerSpawn_All" }),           // trailers and the Scrapwagon
            // items
            ["item_starter"] = ("ItemSpawn_StarterBase", new[] { "ItemSpawn_StarterBase" }),
            ["engine_starter"] = ("ItemSpawn_StarterBase", new[] { "EngineSpawn_StarterBase" }),
            ["item_unique"] = ("ItemSpawnUnique", new[] { "ItemSpawn_Unique" }),     // the one-engine spots in caves, camps, buildings
            ["item_boss"] = ("ItemSpawnUnique", new[] { "ItemSpawn_Unique_BossRoom" }),
            ["trader_merchant"] = ("ItemBuySellList", new[] { "ItemBuySellList" }),  // what merchants sell and buy
            ["trader_mechanic"] = ("ItemBuySellList", new[] { "MechanicItemsSpawn_V1", "Sell_Items_mechanic" }),
            ["trader_medic"] = ("ItemBuySellList", new[] { "OrganicMechanicItemsSpawn_V1", "Sell_Items_organic_mechanic" }),
        };

        /// asset id -> its template and the lists it goes in (table -> copies)
        private static readonly Dictionary<string, (GameObject template, Dictionary<string, int> tables, string like)> Wanted =
            new Dictionary<string, (GameObject, Dictionary<string, int>, string)>(StringComparer.OrdinalIgnoreCase);

        /// Lists that aren't spawn lists (registries of what exists): "spawn like" leaves them alone.
        private static readonly HashSet<string> NotSpawnLists = new HashSet<string>
            { "ItemList", "ArrayList_Cars", "ArrayList_Items", "ArrayList_SpawnersStatics", "ArrayList_Sandstorms" };

        /// Templates replaced by a rebuild: they leave every list (before they're destroyed and leave holes).
        private static readonly HashSet<GameObject> Retired = new HashSet<GameObject>();

        public static void Patch(Harmony harmony)
        {
            var awake = AccessTools.Method(typeof(PlayMakerArrayListProxy), "Awake");
            if (awake == null) { Plugin.Log.LogWarning("Spawning: no PlayMakerArrayListProxy.Awake, spawn lists are filled at scene load only"); return; }
            harmony.Patch(awake, postfix: new HarmonyMethod(typeof(Spawning), nameof(AfterAwake)));
        }

        private static void AfterAwake(PlayMakerArrayListProxy __instance)
        {
            try { Fill(__instance); } catch (Exception e) { Plugin.Log.LogWarning("Spawning: " + e.Message); }
        }

        /// A built asset's spawn lists (spec: spawn = { { table = "car_all", weight = 1 }, ... }).
        public static void Registered(Library.Asset a, Dictionary<string, object> spec, GameObject template)
        {
            var tables = new Dictionary<string, int>();
            foreach (var e in Spec.Entries(spec, "spawn"))
            {
                string t = Json.Str(e, "table");
                if (t == null) continue;
                if (!Tables.ContainsKey(t)) { a.Problems.Add($"spawn: unknown list '{t}'"); continue; }
                tables[t] = Mathf.Clamp((int)Json.Num(e, "weight", 1), 1, 50);
            }
            if (Wanted.TryGetValue(a.Id, out var prev) && prev.template != null && prev.template != template) Retired.Add(prev.template);
            // spawn like a game prefab (spawn_like = "Poloska"): in every spawn list that has it, as often as it is there
            string like = Json.Str(spec, "spawn_like");
            Wanted[a.Id] = (template, tables, string.IsNullOrEmpty(like) ? null : like);
            foreach (var proxy in Object.FindObjectsOfType<PlayMakerArrayListProxy>()) Fill(proxy);
        }

        private static string Spawner(GameObject go)
        {
            string n = go.name.Replace("(Clone)", "").Trim();
            int cut = n.IndexOf(" (", StringComparison.Ordinal);   // "CarSpawn_All (1)"
            return cut > 0 ? n.Substring(0, cut) : n;
        }

        private static void Fill(PlayMakerArrayListProxy proxy)
        {
            var list = proxy.arrayList;
            if (list == null || Wanted.Count == 0) return;
            string spawner = Spawner(proxy.gameObject);
            bool known = Tables.Values.Any(t => t.reference == proxy.referenceName && t.spawners.Contains(spawner));
            bool likeable = !NotSpawnLists.Contains(proxy.referenceName ?? "") && Wanted.Values.Any(w => w.like != null);
            if (!known && !likeable) return;
            // earlier builds of ours (destroyed by a rebuild) leave
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] is GameObject g && (g == null || Retired.Contains(g))) list.RemoveAt(i);
            foreach (var w in Wanted.Values)
            {
                if (w.template == null) continue;
                int want = 0;
                foreach (var kv in w.tables)
                {
                    var t = Tables[kv.Key];
                    if (t.reference == proxy.referenceName && t.spawners.Contains(spawner)) want = Math.Max(want, kv.Value);
                }
                if (w.like != null && likeable)
                {
                    // the game's prefab (not a copy in the world) of that name, as many times as the list has it
                    int n = 0;
                    foreach (var e in list)
                        if (e is GameObject g && g != null && !g.scene.IsValid() && g.name == w.like) n++;
                    want = Math.Max(want, n);
                }
                int have = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(list[i], w.template)) { if (have >= want) list.RemoveAt(i); else have++; }
                for (; have < want; have++) list.Add(w.template);
            }
        }
    }
}
