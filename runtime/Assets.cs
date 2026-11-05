using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Finds the game's assets (meshes, materials, sounds, prefabs, settings) a snapshot refers to by type and name,
    /// using what the snapshot recorded about each to pick the right one when several share a name.
    internal static class Assets
    {
        private static readonly Dictionary<Type, Dictionary<string, List<Object>>> ByType = new Dictionary<Type, Dictionary<string, List<Object>>>();
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        public static readonly List<string> Missing = new List<string>();

        public static void Forget() => ByType.Clear();

        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Types.TryGetValue(name, out var t)) return t;
            t = Type.GetType(name, false);
            if (t == null)
            {
                string full = name.Split(',')[0].Trim();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType(full, false); } catch { }
                    if (t != null) break;
                }
            }
            Types[name] = t;
            return t;
        }

        private static Dictionary<string, List<Object>> Index(Type t)
        {
            if (ByType.TryGetValue(t, out var idx)) return idx;
            idx = new Dictionary<string, List<Object>>();
            foreach (var o in Resources.FindObjectsOfTypeAll(t))
            {
                if (o == null || Gltf.Ours.Contains(o)) continue;   // ours, made from a model: not the game's
                if (!idx.TryGetValue(o.name, out var l)) idx[o.name] = l = new List<Object>();
                l.Add(o);
            }
            ByType[t] = idx;
            return idx;
        }

        /// The game's object for an asset entry of a snapshot, or null.
        public static Object Find(Dictionary<string, object> info)
        {
            var t = FindType(Json.Str(info, "type"));
            string name = Json.Str(info, "name");
            if (t == null || name == null) return null;
            if (!Index(t).TryGetValue(name, out var list) || list.Count == 0)
            {
                Missing.Add($"{t.Name} '{name}'");
                return null;
            }
            if (list.Count == 1) return list[0];
            Object best = null; int bestScore = int.MinValue;
            foreach (var o in list)
            {
                int s = Score(o, info);
                if (s > bestScore) { bestScore = s; best = o; }
            }
            return best;
        }

        private static int Score(Object o, Dictionary<string, object> info)
        {
            int s = 0;
            switch (o)
            {
                case Mesh m:
                    if (m.vertexCount == (int)Json.Num(info, "vertices", -1)) s += 10;
                    if (m.subMeshCount == (int)Json.Num(info, "submeshes", -1)) s += 2;
                    break;
                case Material mat:
                    if (mat.shader != null && mat.shader.name == Json.Str(info, "shader")) s += 5;
                    // the game's has its texture: a copy without it (another mod's, a stripped one) doesn't
                    var texRef = info.TryGetValue("mainTexture", out var tr) ? tr as Dictionary<string, object> : null;
                    string texKey = texRef != null ? Json.Str(texRef, "key") : null;
                    string texName = texKey != null && texKey.Contains("/") ? texKey.Substring(texKey.IndexOf('/') + 1) : null;
                    Texture main = null;
                    try { main = mat.HasProperty("_MainTex") ? mat.mainTexture : null; } catch { }
                    if (texName != null) s += main != null && main.name == texName ? 12 : main == null ? -12 : -4;
                    var kw = Json.Arr(info, "keywords");
                    if (kw != null && mat.shaderKeywords != null && kw.Count == mat.shaderKeywords.Length) s += 1;
                    break;
                case AudioClip clip:
                    if (Math.Abs(clip.length - Json.Num(info, "length", -1)) < 0.01) s += 5;
                    break;
                case Texture tex:
                    if (tex.width == (int)Json.Num(info, "width", -1) && tex.height == (int)Json.Num(info, "height", -1)) s += 5;
                    break;
                case GameObject go:
                    bool prefab = !go.scene.IsValid();
                    if (prefab == Json.Bool(info, "prefab")) s += 10;
                    if (PathOf(go.transform) == Json.Str(info, "path")) s += 5;
                    break;
                case Component c:
                    if (!c.gameObject.scene.IsValid() == Json.Bool(info, "prefab")) s += 10;
                    if (PathOf(c.transform) == Json.Str(info, "path")) s += 5;
                    break;
            }
            return s;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }
    }
}
