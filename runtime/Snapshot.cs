using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Writes objects the way Unity serializes them: a GameObject hierarchy with every component, a script's fields by
    /// Unity's rules (public, or [SerializeField]; [Serializable] classes inside; UnityEngine.Object fields as references),
    /// a built-in component's settings through its properties. A reference to something inside the same hierarchy is a
    /// path; to anything else, an entry in the snapshot's asset table (type, name, and what identifies it).
    internal sealed class Snapshot
    {
        public const int MaxDepth = 14;

        private readonly Transform _root;
        private readonly Dictionary<Object, Dictionary<string, object>> _localRefs = new Dictionary<Object, Dictionary<string, object>>();
        public readonly Dictionary<string, object> Assets = new Dictionary<string, object>();
        private readonly Dictionary<Object, string> _assetKeys = new Dictionary<Object, string>();
        private readonly HashSet<object> _visiting = new HashSet<object>(ReferenceEqualityComparer.Instance);
        public readonly List<string> Problems = new List<string>();

        public Snapshot(Transform root) { _root = root; IndexLocal(root, ""); }

        // ------------------------------------------------------------------ hierarchy

        public Dictionary<string, object> Hierarchy() => Node(_root, "");

        private void IndexLocal(Transform t, string path)
        {
            _localRefs[t.gameObject] = new Dictionary<string, object> { ["$ref"] = "local", ["path"] = path, ["type"] = "GameObject" };
            var byType = new Dictionary<Type, int>();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                var ty = c.GetType();
                byType.TryGetValue(ty, out int n);
                byType[ty] = n + 1;
                _localRefs[c] = new Dictionary<string, object> { ["$ref"] = "local", ["path"] = path, ["type"] = ty.FullName, ["i"] = n };
            }
            for (int i = 0; i < t.childCount; i++) IndexLocal(t.GetChild(i), path.Length == 0 ? i.ToString() : path + "/" + i);
        }

        private Dictionary<string, object> Node(Transform t, string path)
        {
            var go = t.gameObject;
            var n = new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["path"] = path,
                ["tag"] = go.tag,
                ["layer"] = go.layer,
                ["active"] = go.activeSelf,
                ["static"] = go.isStatic,
                ["pos"] = V(t.localPosition),
                ["rot"] = V(t.localRotation),
                ["scale"] = V(t.localScale),
            };
            var comps = new List<object>();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) { comps.Add(new Dictionary<string, object> { ["$type"] = "<missing script>" }); continue; }
                if (c is Transform) continue;
                try { comps.Add(Component(c)); }
                catch (Exception e) { Problems.Add($"{path} {c.GetType().Name}: {e.Message}"); comps.Add(new Dictionary<string, object> { ["$type"] = c.GetType().AssemblyQualifiedName, ["$error"] = e.Message }); }
            }
            n["components"] = comps;
            var kids = new List<object>();
            for (int i = 0; i < t.childCount; i++) kids.Add(Node(t.GetChild(i), path.Length == 0 ? i.ToString() : path + "/" + i));
            n["children"] = kids;
            return n;
        }

        // ------------------------------------------------------------------ components

        public Dictionary<string, object> Component(Component c)
        {
            var ty = c.GetType();
            var d = new Dictionary<string, object> { ["$type"] = TypeName(ty) };
            if (c is Behaviour b) d["enabled"] = b.enabled;
            if (c is MonoBehaviour)
            {
                d["fields"] = Fields(c, ty, 0);
                return d;
            }
            var props = BuiltIn.Read(c, this);
            if (props != null) d["props"] = props;
            return d;
        }

        public static string TypeName(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;

        /// A script's (or [Serializable] class's) fields, by Unity's rules.
        public Dictionary<string, object> Fields(object obj, Type ty, int depth)
        {
            var d = new Dictionary<string, object>();
            foreach (var f in UnityFields(ty))
            {
                object v;
                try { v = f.GetValue(obj); }
                catch (Exception e) { Problems.Add($"{ty.Name}.{f.Name}: {e.Message}"); continue; }
                d[f.Name] = Value(v, f.FieldType, depth + 1);
            }
            return d;
        }

        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

        /// The fields Unity serializes on a type: instance fields, public and not [NonSerialized] or non-public with
        /// [SerializeField] / [SerializeReference], of a type Unity can serialize, base classes first.
        public static FieldInfo[] UnityFields(Type ty)
        {
            if (FieldCache.TryGetValue(ty, out var cached)) return cached;
            var chain = new List<Type>();
            for (var t = ty; t != null && t != typeof(object) && t != typeof(MonoBehaviour) && t != typeof(ScriptableObject)
                            && t != typeof(Behaviour) && t != typeof(Component) && t != typeof(Object); t = t.BaseType)
                chain.Insert(0, t);
            var list = new List<FieldInfo>();
            var seen = new HashSet<string>();
            foreach (var t in chain)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsInitOnly || f.IsLiteral) continue;
                    if (f.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                    bool serRef = f.IsDefined(typeof(SerializeReference), false);
                    if (!f.IsPublic && !f.IsDefined(typeof(SerializeField), false) && !serRef) continue;
                    if (!serRef && !Serializable(f.FieldType)) continue;
                    if (!seen.Add(f.Name)) continue; // a hidden base field of the same name: Unity keeps the first
                    list.Add(f);
                }
            var arr = list.ToArray();
            FieldCache[ty] = arr;
            return arr;
        }

        public static bool Serializable(Type t, bool inCollection = false)
        {
            if (t.IsPrimitive || t.IsEnum || t == typeof(string)) return true;
            if (typeof(Object).IsAssignableFrom(t)) return true;
            if (UnityStruct(t)) return true;
            if (t == typeof(AnimationCurve) || t == typeof(Gradient) || t == typeof(LayerMask)) return true;
            if (t.IsArray)
                return !inCollection && t.GetArrayRank() == 1 && Serializable(t.GetElementType(), true);
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                return !inCollection && Serializable(t.GetGenericArguments()[0], true);
            if (t.IsAbstract || t.IsInterface || typeof(Delegate).IsAssignableFrom(t)) return false;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return false;
            return t.IsDefined(typeof(SerializableAttribute), false) && (t.IsClass || t.IsValueType) && !t.IsPointer;
        }

        public static bool UnityStruct(Type t) =>
            t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color)
            || t == typeof(Color32) || t == typeof(Rect) || t == typeof(Bounds) || t == typeof(Vector2Int) || t == typeof(Vector3Int)
            || t == typeof(Matrix4x4) || t == typeof(RectInt) || t == typeof(BoundsInt);

        // ------------------------------------------------------------------ values

        public object Value(object v, Type declared, int depth)
        {
            if (v == null) return null;
            var t = v.GetType();
            switch (v)
            {
                case string s: return s;
                case bool b: return b;
                case float f: return f;
                case double d: return d;
                case int i: return i;
                case long l: return l;
                case uint ui: return ui;
                case ulong ul: return ul;
                case short sh: return sh;
                case ushort us: return us;
                case byte by: return by;
                case sbyte sb: return sb;
                case char ch: return (int)ch;
                case Enum e: return Convert.ToInt64(e);
                case Vector2 a: return new List<object> { a.x, a.y };
                case Vector3 a: return V(a);
                case Vector4 a: return new List<object> { a.x, a.y, a.z, a.w };
                case Quaternion a: return V(a);
                case Color a: return new List<object> { a.r, a.g, a.b, a.a };
                case Color32 a: return new List<object> { (int)a.r, (int)a.g, (int)a.b, (int)a.a };
                case Rect a: return new List<object> { a.x, a.y, a.width, a.height };
                case Bounds a: return new Dictionary<string, object> { ["c"] = V(a.center), ["e"] = V(a.extents) };
                case Vector2Int a: return new List<object> { a.x, a.y };
                case Vector3Int a: return new List<object> { a.x, a.y, a.z };
                case RectInt a: return new List<object> { a.x, a.y, a.width, a.height };
                case BoundsInt a: return new Dictionary<string, object> { ["p"] = new List<object> { a.x, a.y, a.z }, ["s"] = new List<object> { a.size.x, a.size.y, a.size.z } };
                case Matrix4x4 m: { var l = new List<object>(); for (int i = 0; i < 16; i++) l.Add(m[i]); return l; }
                case LayerMask lm: return lm.value;
                case AnimationCurve curve: return Curve(curve);
                case Gradient g: return Grad(g);
                case byte[] bytes: return new Dictionary<string, object> { ["$b64"] = Convert.ToBase64String(bytes) };
                case List<byte> blist: return new Dictionary<string, object> { ["$b64"] = Convert.ToBase64String(blist.ToArray()) };
                case Object uo: return Ref(uo);
            }
            if (v is IList list)
            {
                var el = t.IsArray ? t.GetElementType() : t.GetGenericArguments().FirstOrDefault() ?? typeof(object);
                var outList = new List<object>(list.Count);
                foreach (var e in list) outList.Add(Value(e, el, depth));
                return outList;
            }
            if (depth > MaxDepth) return new Dictionary<string, object> { ["$depth"] = true };
            if (!t.IsValueType && !_visiting.Add(v)) return new Dictionary<string, object> { ["$cycle"] = true };
            try
            {
                var d = Fields(v, t, depth);
                if (declared == null || t != declared) d["$type"] = TypeName(t);
                return d;
            }
            finally { if (!t.IsValueType) _visiting.Remove(v); }
        }

        public static List<object> V(Vector3 a) => new List<object> { a.x, a.y, a.z };
        public static List<object> V(Quaternion a) => new List<object> { a.x, a.y, a.z, a.w };

        private static Dictionary<string, object> Curve(AnimationCurve c)
        {
            var keys = new List<object>();
            foreach (var k in c.keys)
                keys.Add(new List<object> { k.time, k.value, k.inTangent, k.outTangent, k.inWeight, k.outWeight, (int)k.weightedMode });
            return new Dictionary<string, object> { ["keys"] = keys, ["pre"] = (int)c.preWrapMode, ["post"] = (int)c.postWrapMode };
        }

        private static Dictionary<string, object> Grad(Gradient g)
        {
            var cs = g.colorKeys.Select(k => (object)new List<object> { k.color.r, k.color.g, k.color.b, k.time }).ToList();
            var al = g.alphaKeys.Select(k => (object)new List<object> { k.alpha, k.time }).ToList();
            return new Dictionary<string, object> { ["c"] = cs, ["a"] = al, ["mode"] = (int)g.mode };
        }

        // ------------------------------------------------------------------ references

        public object Ref(Object o)
        {
            if (o == null) return null; // a destroyed or missing reference reads as null too
            if (_localRefs.TryGetValue(o, out var local)) return local;
            return new Dictionary<string, object> { ["$ref"] = "asset", ["key"] = AssetKey(o) };
        }

        private string AssetKey(Object o)
        {
            if (_assetKeys.TryGetValue(o, out var key)) return key;
            var ty = o.GetType();
            string baseKey = ty.Name + "/" + o.name;
            key = baseKey;
            for (int n = 2; Assets.ContainsKey(key); n++) key = baseKey + "#" + n;
            _assetKeys[o] = key;
            var info = new Dictionary<string, object> { ["type"] = TypeName(ty), ["name"] = o.name };
            Assets[key] = info; // registered before describing it: a ScriptableObject can refer back to itself
            try { Describe(o, info); }
            catch (Exception e) { info["$error"] = e.Message; }
            return key;
        }

        /// What identifies an asset (and, for data assets, everything in it).
        private void Describe(Object o, Dictionary<string, object> info)
        {
            switch (o)
            {
                case Mesh m:
                    info["vertices"] = m.vertexCount;
                    info["submeshes"] = m.subMeshCount;
                    info["bounds"] = Value(m.bounds, typeof(Bounds), 0);
                    info["readable"] = m.isReadable;
                    break;
                case Material mat:
                    info["shader"] = mat.shader != null ? mat.shader.name : null;
                    if (mat.HasProperty("_Color")) info["color"] = Value(mat.color, typeof(Color), 0);
                    if (mat.HasProperty("_MainTex")) info["mainTexture"] = mat.mainTexture != null ? Ref(mat.mainTexture) : null;
                    info["keywords"] = mat.shaderKeywords.Cast<object>().ToList();
                    info["renderQueue"] = mat.renderQueue;
                    break;
                case Texture tex:
                    info["width"] = tex.width;
                    info["height"] = tex.height;
                    break;
                case AudioClip clip:
                    info["length"] = clip.length;
                    info["channels"] = clip.channels;
                    info["frequency"] = clip.frequency;
                    break;
                case PhysicMaterial pm:
                    info["dynamicFriction"] = pm.dynamicFriction;
                    info["staticFriction"] = pm.staticFriction;
                    info["bounciness"] = pm.bounciness;
                    info["frictionCombine"] = (int)pm.frictionCombine;
                    info["bounceCombine"] = (int)pm.bounceCombine;
                    break;
                case GameObject go:
                    info["prefab"] = !go.scene.IsValid();
                    info["tag"] = go.tag;
                    info["path"] = ScenePath(go.transform);
                    break;
                case Component comp:
                    info["gameObject"] = comp.gameObject.name;
                    info["prefab"] = !comp.gameObject.scene.IsValid();
                    info["path"] = ScenePath(comp.transform);
                    break;
                case ScriptableObject so:
                    info["fields"] = Fields(so, so.GetType(), 0);
                    break;
            }
        }

        private static string ScenePath(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
