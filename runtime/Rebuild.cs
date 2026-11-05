using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Builds objects from snapshots (the reverse of Snapshot). In two passes: first every GameObject and component of
    /// the subtrees asked for (so references between them can be resolved), then every component's settings.
    /// Subtrees can come from several snapshots and be put anywhere; a reference to something of the same snapshot
    /// that wasn't built resolves to null (and is reported), unless Redirect says what stands in for it.
    internal sealed class Rebuild
    {
        public sealed class Source
        {
            public string Name;
            public Dictionary<string, object> Assets;
            public readonly Dictionary<string, Transform> Nodes = new Dictionary<string, Transform>();
            /// Every node of the snapshot by path (built or not).
            public readonly Dictionary<string, Dictionary<string, object>> Index = new Dictionary<string, Dictionary<string, object>>();

            /// "a/b/c": the names from the snapshot's root down to a path ("" for the root itself).
            public string NamePath(string path)
            {
                if (string.IsNullOrEmpty(path)) return "";
                var names = new List<string>();
                string p = path;
                while (!string.IsNullOrEmpty(p))
                {
                    names.Insert(0, Index.TryGetValue(p, out var n) ? Json.Str(n, "name") : "?");
                    int cut = p.LastIndexOf('/');
                    p = cut < 0 ? "" : p.Substring(0, cut);
                }
                return string.Join("/", names);
            }
            internal readonly Dictionary<string, Object> Resolved = new Dictionary<string, Object>();
        }

        private struct Pending { public Component C; public Dictionary<string, object> Entry; public Source Src; public string Path; }

        private readonly List<Pending> _pending = new List<Pending>();
        public readonly List<string> Problems = new List<string>();
        /// (source, path) of an object that wasn't built -> what to use instead (null: nothing).
        public Func<Source, string, Transform> Redirect;
        /// A "named" reference ({ "$ref": "named", "path": "a/b" }, written by the assembler) -> the object it means.
        public Func<string, Transform> Named;

        public Source Load(string name, Dictionary<string, object> doc)
        {
            var src = new Source { Name = name, Assets = Json.Obj(doc, "assets") ?? new Dictionary<string, object>() };
            void Walk(Dictionary<string, object> n)
            {
                if (n == null) return;
                src.Index[Json.Str(n, "path", "")] = n;
                foreach (var c in Json.Arr(n, "children") ?? new List<object>()) Walk(c as Dictionary<string, object>);
            }
            Walk(Json.Obj(doc, "root"));
            return src;
        }

        // ------------------------------------------------------------------ pass 1: objects and components

        /// The GameObject (and, unless skip says no, its children) of a snapshot node, under parent.
        public GameObject Create(Source src, Dictionary<string, object> node, Transform parent, Func<Dictionary<string, object>, bool> keep = null)
        {
            string path = Json.Str(node, "path", "");
            var go = new GameObject(Json.Str(node, "name", "GameObject"));
            // a root is built switched off, so nothing on it wakes up until it's all built (it is then a prefab:
            // copies of it are switched on); below it, objects are on or off as in the snapshot
            if (parent != null) go.transform.SetParent(parent, false);
            go.SetActive(parent != null && Json.Bool(node, "active", true));
            try { go.tag = Json.Str(node, "tag", "Untagged"); } catch { Problems.Add($"{src.Name}:{path}: unknown tag '{Json.Str(node, "tag")}'"); }
            go.layer = (int)Json.Num(node, "layer", 0);
            go.isStatic = Json.Bool(node, "static");
            var t = go.transform;
            t.localPosition = V3(node["pos"]);
            t.localRotation = Q(node["rot"]);
            t.localScale = V3(node["scale"]);
            src.Nodes[path] = t;

            foreach (var ce in Json.Arr(node, "components") ?? new List<object>())
            {
                var entry = ce as Dictionary<string, object>;
                string typeName = Json.Str(entry, "$type");
                var type = Assets.FindType(typeName);
                if (type == null) { Problems.Add($"{src.Name}:{path}: no component type {typeName}"); continue; }
                if (!typeof(Component).IsAssignableFrom(type) || typeof(Transform).IsAssignableFrom(type)) continue;
                Component c = null;
                // a component another one [RequireComponent]ed is already there: use it rather than add a second
                var existing = go.GetComponents(type);
                foreach (var e in existing)
                    if (!_pending.Any(p => p.C == e)) { c = e; break; }
                if (c == null)
                {
                    try { c = go.AddComponent(type); }
                    catch (Exception ex) { Problems.Add($"{src.Name}:{path}: {type.Name} not added: {ex.Message}"); }
                }
                if (c == null) { Problems.Add($"{src.Name}:{path}: {type.Name} not added"); continue; }
                _pending.Add(new Pending { C = c, Entry = entry, Src = src, Path = path });
            }

            foreach (var ch in Json.Arr(node, "children") ?? new List<object>())
            {
                var cn = ch as Dictionary<string, object>;
                if (keep != null && !keep(cn)) continue;
                Create(src, cn, t, keep);
            }
            return go;
        }

        // ------------------------------------------------------------------ pass 2: settings

        public void Apply()
        {
            foreach (var p in _pending)
            {
                if (p.C == null) continue;
                try { ApplyComponent(p); }
                catch (Exception e) { Problems.Add($"{p.Src.Name}:{p.Path}: {p.C.GetType().Name}: {e.Message}"); }
            }
            _pending.Clear();
        }

        private void ApplyComponent(Pending p)
        {
            var entry = p.Entry;
            if (p.C is Behaviour b && entry.TryGetValue("enabled", out var en) && en is bool on) b.enabled = on;
            if (p.C is MonoBehaviour)
            {
                var fields = Json.Obj(entry, "fields");
                if (fields != null) SetFields(p.C, p.C.GetType(), fields, p.Src, p.Path);
                return;
            }
            var props = Json.Obj(entry, "props");
            if (props == null) return;
            var t = p.C.GetType();
            foreach (var kv in props)
            {
                if (kv.Key.StartsWith("$") || kv.Key == "centerOfMass") continue; // centre of mass: Unity works it out unless a script sets it
                var prop = t.GetProperty(kv.Key, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null || !prop.CanWrite) continue;
                if (kv.Value is Dictionary<string, object> err && err.ContainsKey("$error")) continue;
                try { prop.SetValue(p.C, Value(kv.Value, prop.PropertyType, p.Src, p.Path, true), null); }
                catch (Exception e) { Problems.Add($"{p.Src.Name}:{p.Path}: {t.Name}.{kv.Key}: {(e.InnerException ?? e).Message}"); }
            }
        }

        private void SetFields(object target, Type type, Dictionary<string, object> fields, Source src, string path)
        {
            foreach (var f in Snapshot.UnityFields(type))
            {
                if (!fields.TryGetValue(f.Name, out var raw)) continue;
                if (raw is Dictionary<string, object> marker && (marker.ContainsKey("$depth") || marker.ContainsKey("$cycle"))) continue;
                try { f.SetValue(target, Value(raw, f.FieldType, src, path, false)); }
                catch (Exception e) { Problems.Add($"{src.Name}:{path}: {type.Name}.{f.Name}: {(e.InnerException ?? e).Message}"); }
            }
            // as Unity does once it has loaded an object (dictionaries kept as key and value lists rebuild themselves...)
            if (target is ISerializationCallbackReceiver cb)
                try { cb.OnAfterDeserialize(); } catch (Exception e) { Problems.Add($"{src.Name}:{path}: {type.Name}.OnAfterDeserialize: {e.Message}"); }
        }

        // ------------------------------------------------------------------ values

        public object Value(object raw, Type t, Source src, string path, bool property)
        {
            if (raw == null)
            {
                // Unity never leaves a [Serializable] class field null: it makes one
                if (!property && t.IsClass && t != typeof(string) && !typeof(Object).IsAssignableFrom(t) && !t.IsArray
                    && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) && t.IsDefined(typeof(SerializableAttribute), false)
                    && !t.IsAbstract)
                    return New(t);
                return t.IsValueType ? Activator.CreateInstance(t) : null;
            }
            if (t == typeof(string)) return raw as string ?? Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return raw is bool b ? b : Json.ToDouble(raw) != 0;
            if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(Json.ToDouble(raw)));
            if (t.IsPrimitive)
            {
                double d = Json.ToDouble(raw);
                if (t == typeof(float)) return (float)d;
                if (t == typeof(double)) return d;
                if (t == typeof(char)) return (char)(int)d;
                if (raw is long l && t == typeof(long)) return l;
                if (raw is long ul && t == typeof(ulong)) return unchecked((ulong)ul);
                return Convert.ChangeType(d, t, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (t == typeof(Vector2)) { var a = F(raw); return new Vector2(a[0], a[1]); }
            if (t == typeof(Vector3)) return V3(raw);
            if (t == typeof(Vector4)) { var a = F(raw); return new Vector4(a[0], a[1], a[2], a[3]); }
            if (t == typeof(Quaternion)) return Q(raw);
            if (t == typeof(Color)) { var a = F(raw); return new Color(a[0], a[1], a[2], a[3]); }
            if (t == typeof(Color32)) { var a = F(raw); return new Color32((byte)a[0], (byte)a[1], (byte)a[2], (byte)a[3]); }
            if (t == typeof(Rect)) { var a = F(raw); return new Rect(a[0], a[1], a[2], a[3]); }
            if (t == typeof(Vector2Int)) { var a = F(raw); return new Vector2Int((int)a[0], (int)a[1]); }
            if (t == typeof(Vector3Int)) { var a = F(raw); return new Vector3Int((int)a[0], (int)a[1], (int)a[2]); }
            if (t == typeof(RectInt)) { var a = F(raw); return new RectInt((int)a[0], (int)a[1], (int)a[2], (int)a[3]); }
            if (t == typeof(Bounds)) { var o = (Dictionary<string, object>)raw; return new Bounds(V3(o["c"]), V3(o["e"]) * 2f); }
            if (t == typeof(Matrix4x4)) { var a = F(raw); var m = new Matrix4x4(); for (int i = 0; i < 16; i++) m[i] = a[i]; return m; }
            if (t == typeof(LayerMask)) return (LayerMask)(int)Json.ToDouble(raw);
            if (t == typeof(AnimationCurve)) return Curve((Dictionary<string, object>)raw);
            if (t == typeof(Gradient)) return Grad((Dictionary<string, object>)raw);

            if (raw is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("$b64", out var b64))
                {
                    var bytes = Convert.FromBase64String((string)b64);
                    if (t == typeof(byte[])) return bytes;
                    if (t == typeof(List<byte>)) return new List<byte>(bytes);
                }
                if (dict.TryGetValue("$ref", out var kind))
                    return Ref(dict, (string)kind, t, src, path);
            }
            if (typeof(Object).IsAssignableFrom(t)) return null;

            if (raw is List<object> list)
            {
                if (t.IsArray)
                {
                    var et = t.GetElementType();
                    var arr = Array.CreateInstance(et, list.Count);
                    for (int i = 0; i < list.Count; i++) arr.SetValue(Value(list[i], et, src, path, property), i);
                    return arr;
                }
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var et = t.GetGenericArguments()[0];
                    var l = (IList)Activator.CreateInstance(t);
                    foreach (var e in list) l.Add(Value(e, et, src, path, property));
                    return l;
                }
            }

            if (raw is Dictionary<string, object> obj)
            {
                var actual = t;
                if (obj.TryGetValue("$type", out var tn)) actual = Assets.FindType((string)tn) ?? t;
                if (property && actual.IsValueType)
                {
                    // a built-in struct (JointDrive, SoftJointLimit...): its properties
                    object box = Activator.CreateInstance(actual);
                    foreach (var kv in obj)
                    {
                        var pr = actual.GetProperty(kv.Key, BindingFlags.Instance | BindingFlags.Public);
                        if (pr != null && pr.CanWrite) { try { pr.SetValue(box, Value(kv.Value, pr.PropertyType, src, path, true), null); } catch { } continue; }
                        var fi = actual.GetField(kv.Key, BindingFlags.Instance | BindingFlags.Public);
                        if (fi != null) try { fi.SetValue(box, Value(kv.Value, fi.FieldType, src, path, true)); } catch { }
                    }
                    return box;
                }
                object inst = actual.IsValueType ? Activator.CreateInstance(actual) : New(actual);
                if (inst == null) return null;
                SetFields(inst, actual, obj, src, path);
                return inst;
            }
            return null;
        }

        private static object New(Type t)
        {
            try { return Activator.CreateInstance(t, true); }
            catch { try { return FormatterServices.GetUninitializedObject(t); } catch { return null; } }
        }

        private object Ref(Dictionary<string, object> r, string kind, Type wanted, Source src, string fromPath)
        {
            if (kind == "local" || kind == "named")
            {
                string path = Json.Str(r, "path", "");
                Transform t = null;
                if (kind == "named") t = Named?.Invoke(path);
                else if (!src.Nodes.TryGetValue(path, out t) || t == null) t = Redirect?.Invoke(src, path);
                if (t == null) { Problems.Add($"{src.Name}:{fromPath}: refers to '{(kind == "named" ? path : src.NamePath(path))}', not built"); return null; }
                string typeName = Json.Str(r, "type");
                if (typeName == "GameObject") return Fit(t.gameObject, wanted);
                var ct = Assets.FindType(typeName);
                if (ct == null) return null;
                if (typeof(Transform).IsAssignableFrom(ct)) return Fit(t, wanted);
                var all = t.GetComponents(ct);
                int i = (int)Json.Num(r, "i", 0);
                return Fit(i < all.Length ? all[i] : all.FirstOrDefault(), wanted);
            }
            if (kind == "asset")
            {
                string key = Json.Str(r, "key");
                if (key == null) return null;
                if (!src.Resolved.TryGetValue(key, out var o))
                {
                    var info = Json.Obj(src.Assets, key);
                    o = info != null ? Assets.Find(info) : null;
                    src.Resolved[key] = o;
                }
                return Fit(o, wanted);
            }
            return null;
        }

        /// A reference recorded as one kind of object, asked for as another (a GameObject where a field wants its Transform...).
        private static object Fit(Object o, Type wanted)
        {
            if (o == null) return null;
            if (wanted.IsInstanceOfType(o)) return o;
            var go = o as GameObject ?? (o as Component)?.gameObject;
            if (go != null && wanted == typeof(GameObject)) return go;
            if (go != null && typeof(Component).IsAssignableFrom(wanted)) return go.GetComponent(wanted);
            return wanted.IsInstanceOfType(o) ? o : null;
        }

        // ------------------------------------------------------------------ small things

        private static float[] F(object raw) => ((List<object>)raw).Select(x => (float)Json.ToDouble(x)).ToArray();
        public static Vector3 V3(object raw) { var a = F(raw); return new Vector3(a[0], a[1], a[2]); }
        public static Quaternion Q(object raw) { var a = F(raw); return new Quaternion(a[0], a[1], a[2], a[3]); }

        private static AnimationCurve Curve(Dictionary<string, object> o)
        {
            var keys = (Json.Arr(o, "keys") ?? new List<object>()).Select(k =>
            {
                var a = F(k);
                return new Keyframe(a[0], a[1], a[2], a[3], a[4], a[5]) { weightedMode = (WeightedMode)(int)a[6] };
            }).ToArray();
            return new AnimationCurve(keys) { preWrapMode = (WrapMode)(int)Json.Num(o, "pre"), postWrapMode = (WrapMode)(int)Json.Num(o, "post") };
        }

        private static Gradient Grad(Dictionary<string, object> o)
        {
            var g = new Gradient();
            var cs = (Json.Arr(o, "c") ?? new List<object>()).Select(k => { var a = F(k); return new GradientColorKey(new Color(a[0], a[1], a[2]), a[3]); }).ToArray();
            var al = (Json.Arr(o, "a") ?? new List<object>()).Select(k => { var a = F(k); return new GradientAlphaKey(a[0], a[1]); }).ToArray();
            g.SetKeys(cs, al);
            g.mode = (GradientMode)(int)Json.Num(o, "mode");
            return g;
        }
    }
}
