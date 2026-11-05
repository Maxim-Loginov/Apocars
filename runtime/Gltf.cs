using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// A .glb (binary glTF 2.0, as Blender exports it) as Unity objects: a hierarchy of GameObjects with their meshes
    /// and materials, each node's custom properties (extras) kept. glTF is right-handed, Unity left-handed: X is
    /// mirrored (positions, normals and tangents negate x, rotations negate y and z, triangles turn round), which keeps
    /// glTF's +Y up and +Z front as Unity's.
    internal sealed class Gltf
    {
        /// Every mesh, material and texture Apocars makes from a model: never taken for one of the game's own, even
        /// when it has the same name (a game material named in a model, a vanilla part's mesh).
        public static readonly HashSet<Object> Ours = new HashSet<Object>();

        /// Made for this model: destroyed with it, never taken for one of the game's own.
        public void Own(Object o)
        {
            Created.Add(o);
            Ours.Add(o);
        }

        public sealed class Node
        {
            public string Name;
            public int Index;
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
            public Vector3 Position = Vector3.zero;
            public Quaternion Rotation = Quaternion.identity;
            public Vector3 Scale = Vector3.one;
            public int Mesh = -1;
            public Dictionary<string, object> Extras;
            public GameObject Object;   // once built
        }

        public string Path;
        public readonly List<Node> Nodes = new List<Node>();
        public readonly List<Node> Roots = new List<Node>();
        private Dictionary<string, object> _json;
        private byte[] _bin;
        private readonly Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();
        private readonly Dictionary<int, Material> _materials = new Dictionary<int, Material>();
        private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        public readonly List<Object> Created = new List<Object>();  // meshes, materials and textures made: destroyed with Unload
        public readonly List<string> Problems = new List<string>();

        // ------------------------------------------------------------------ reading

        public static Gltf Load(string path)
        {
            var g = new Gltf { Path = path };
            var data = File.ReadAllBytes(path);
            if (data.Length < 20 || Encoding.ASCII.GetString(data, 0, 4) != "glTF") throw new FormatException(path + ": not a .glb");
            int p = 12;
            while (p + 8 <= data.Length)
            {
                int len = BitConverter.ToInt32(data, p), kind = BitConverter.ToInt32(data, p + 4);
                if (kind == 0x4E4F534A) g._json = (Dictionary<string, object>)Json.Read(Encoding.UTF8.GetString(data, p + 8, len));
                else if (kind == 0x004E4942) { g._bin = new byte[len]; Buffer.BlockCopy(data, p + 8, g._bin, 0, len); }
                p += 8 + len;
            }
            if (g._json == null) throw new FormatException(path + ": no JSON chunk");
            g.ReadNodes();
            return g;
        }

        private void ReadNodes()
        {
            var nodes = Json.Arr(_json, "nodes") ?? new List<object>();
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = (Dictionary<string, object>)nodes[i];
                var node = new Node { Index = i, Name = Json.Str(n, "name", "node" + i), Mesh = (int)Json.Num(n, "mesh", -1), Extras = Json.Obj(n, "extras") };
                if (n.TryGetValue("matrix", out var m)) Decompose(F((List<object>)m), node);
                else
                {
                    if (n.TryGetValue("translation", out var t)) { var a = F((List<object>)t); node.Position = new Vector3(-a[0], a[1], a[2]); }
                    if (n.TryGetValue("rotation", out var r)) { var a = F((List<object>)r); node.Rotation = new Quaternion(a[0], -a[1], -a[2], a[3]); }
                    if (n.TryGetValue("scale", out var s)) { var a = F((List<object>)s); node.Scale = new Vector3(a[0], a[1], a[2]); }
                }
                Nodes.Add(node);
            }
            for (int i = 0; i < nodes.Count; i++)
                foreach (var c in Json.Arr(nodes[i], "children") ?? new List<object>())
                {
                    var child = Nodes[(int)Json.ToDouble(c)];
                    child.Parent = Nodes[i];
                    Nodes[i].Children.Add(child);
                }
            var scenes = Json.Arr(_json, "scenes");
            int scene = (int)Json.Num(_json, "scene", 0);
            var roots = scenes != null && scene < scenes.Count ? Json.Arr(scenes[scene], "nodes") : null;
            if (roots != null) foreach (var r in roots) Roots.Add(Nodes[(int)Json.ToDouble(r)]);
            else Roots.AddRange(Nodes.Where(n => n.Parent == null));
        }

        private static void Decompose(float[] m, Node node)
        {
            // column-major 4x4
            var mat = new Matrix4x4();
            for (int i = 0; i < 16; i++) mat[i % 4, i / 4] = m[i];
            Vector3 pos = mat.GetColumn(3);
            Vector3 sx = mat.GetColumn(0), sy = mat.GetColumn(1), sz = mat.GetColumn(2);
            var scale = new Vector3(sx.magnitude, sy.magnitude, sz.magnitude);
            var rot = Quaternion.LookRotation(sz / Mathf.Max(scale.z, 1e-6f), sy / Mathf.Max(scale.y, 1e-6f));
            node.Position = new Vector3(-pos.x, pos.y, pos.z);
            node.Rotation = new Quaternion(rot.x, -rot.y, -rot.z, rot.w);
            node.Scale = scale;
        }

        public Node Find(string name) => Nodes.FirstOrDefault(n => n.Name == name);

        // ------------------------------------------------------------------ building

        /// Every node as a GameObject under parent (meshes and materials made once and shared between nodes).
        public GameObject Build(Transform parent, string rootName, Func<Node, bool> keep = null)
        {
            var root = new GameObject(rootName);
            root.transform.SetParent(parent, false);
            foreach (var r in Roots) BuildNode(r, root.transform, keep);
            return root;
        }

        private void BuildNode(Node n, Transform parent, Func<Node, bool> keep)
        {
            if (keep != null && !keep(n)) return;
            var go = new GameObject(n.Name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = n.Position;
            go.transform.localRotation = n.Rotation;
            go.transform.localScale = n.Scale;
            n.Object = go;
            if (n.Mesh >= 0)
            {
                var mesh = MeshOf(n.Mesh, out var mats);
                if (mesh != null)
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = mats;
                }
            }
            foreach (var c in n.Children) BuildNode(c, go.transform, keep);
        }

        /// A node's mesh as one Unity mesh (a submesh per glTF primitive) and its materials.
        public Mesh MeshOf(int index, out Material[] materials)
        {
            var meshes = Json.Arr(_json, "meshes");
            var m = (Dictionary<string, object>)meshes[index];
            var prims = Json.Arr(m, "primitives") ?? new List<object>();
            materials = prims.Select(pr => MaterialOf((int)Json.Num(pr, "material", -1))).ToArray();
            if (_meshes.TryGetValue(index, out var cached)) return cached;

            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var tan = new List<Vector4>();
            var uv0 = new List<Vector2>(); var uv1 = new List<Vector2>(); var col = new List<Color>();
            var tris = new List<int[]>();
            bool hasN = true, hasT = true, hasUv0 = true, hasUv1 = true, hasCol = true;
            foreach (Dictionary<string, object> pr in prims)
            {
                var attrs = Json.Obj(pr, "attributes");
                int mode = (int)Json.Num(pr, "mode", 4);
                if (mode != 4) { Problems.Add($"mesh '{Json.Str(m, "name")}': primitive mode {mode} (only triangles are read)"); tris.Add(new int[0]); continue; }
                int start = pos.Count;
                var p = Vec3(Attr(attrs, "POSITION"));
                int n = p.Length;
                pos.AddRange(p.Select(v => new Vector3(-v.x, v.y, v.z)));
                Fill(nrm, Attr(attrs, "NORMAL") is int ni ? Vec3(ni).Select(v => new Vector3(-v.x, v.y, v.z)).ToArray() : null, n, ref hasN);
                Fill(tan, Attr(attrs, "TANGENT") is int ti ? Vec4(ti).Select(v => new Vector4(-v.x, v.y, v.z, -v.w)).ToArray() : null, n, ref hasT);
                Fill(uv0, Attr(attrs, "TEXCOORD_0") is int u0 ? Vec2(u0).Select(v => new Vector2(v.x, 1f - v.y)).ToArray() : null, n, ref hasUv0);
                Fill(uv1, Attr(attrs, "TEXCOORD_1") is int u1 ? Vec2(u1).Select(v => new Vector2(v.x, 1f - v.y)).ToArray() : null, n, ref hasUv1);
                Fill(col, Attr(attrs, "COLOR_0") is int ci ? Colors(ci) : null, n, ref hasCol);
                int[] idx = pr.ContainsKey("indices") ? Ints((int)Json.Num(pr, "indices")) : Enumerable.Range(0, n).ToArray();
                var t = new int[idx.Length - idx.Length % 3];
                for (int i = 0; i + 2 < idx.Length; i += 3) { t[i] = idx[i] + start; t[i + 1] = idx[i + 2] + start; t[i + 2] = idx[i + 1] + start; }
                tris.Add(t);
            }
            var mesh = new Mesh { name = Json.Str(m, "name", "mesh" + index) };
            if (pos.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(pos);
            if (hasN && nrm.Count == pos.Count) mesh.SetNormals(nrm);
            if (hasUv0 && uv0.Count == pos.Count) mesh.SetUVs(0, uv0);
            if (hasUv1 && uv1.Count == pos.Count) mesh.SetUVs(1, uv1);
            if (hasCol && col.Count == pos.Count) mesh.SetColors(col);
            mesh.subMeshCount = tris.Count;
            for (int s = 0; s < tris.Count; s++) mesh.SetTriangles(tris[s], s, false);
            if (!(hasN && nrm.Count == pos.Count)) mesh.RecalculateNormals();
            if (hasT && tan.Count == pos.Count) mesh.SetTangents(tan);
            else if (hasUv0 && uv0.Count == pos.Count) mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            _meshes[index] = mesh;
            Own(mesh);
            return mesh;
        }

        private static void Fill<T>(List<T> into, T[] values, int n, ref bool has)
        {
            if (values == null || values.Length != n) { has = false; into.AddRange(new T[n]); return; }
            into.AddRange(values);
        }

        private static int? Attr(Dictionary<string, object> attrs, string name) =>
            attrs != null && attrs.TryGetValue(name, out var v) ? (int?)(int)Json.ToDouble(v) : null;

        // ------------------------------------------------------------------ accessors

        private float[] Floats(int accessor, out int components)
        {
            var a = (Dictionary<string, object>)Json.Arr(_json, "accessors")[accessor];
            components = Components(Json.Str(a, "type"));
            int count = (int)Json.Num(a, "count");
            int ctype = (int)Json.Num(a, "componentType");
            bool norm = Json.Bool(a, "normalized");
            var result = new float[count * components];
            if (!a.ContainsKey("bufferView")) return result; // all zero (sparse accessors aren't read)
            var bv = (Dictionary<string, object>)Json.Arr(_json, "bufferViews")[(int)Json.Num(a, "bufferView")];
            int size = ComponentSize(ctype);
            int stride = (int)Json.Num(bv, "byteStride", 0);
            if (stride == 0) stride = size * components;
            int baseOff = (int)Json.Num(bv, "byteOffset", 0) + (int)Json.Num(a, "byteOffset", 0);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < components; c++)
                    result[i * components + c] = Read(ctype, baseOff + i * stride + c * size, norm);
            return result;
        }

        private float Read(int ctype, int off, bool norm)
        {
            switch (ctype)
            {
                case 5126: return BitConverter.ToSingle(_bin, off);
                case 5121: return norm ? _bin[off] / 255f : _bin[off];
                case 5120: return norm ? Mathf.Max((sbyte)_bin[off] / 127f, -1f) : (sbyte)_bin[off];
                case 5123: return norm ? BitConverter.ToUInt16(_bin, off) / 65535f : BitConverter.ToUInt16(_bin, off);
                case 5122: return norm ? Mathf.Max(BitConverter.ToInt16(_bin, off) / 32767f, -1f) : BitConverter.ToInt16(_bin, off);
                case 5125: return BitConverter.ToUInt32(_bin, off);
                default: return 0f;
            }
        }

        private int[] Ints(int accessor)
        {
            var a = (Dictionary<string, object>)Json.Arr(_json, "accessors")[accessor];
            int count = (int)Json.Num(a, "count"), ctype = (int)Json.Num(a, "componentType");
            var bv = (Dictionary<string, object>)Json.Arr(_json, "bufferViews")[(int)Json.Num(a, "bufferView")];
            int size = ComponentSize(ctype);
            int stride = (int)Json.Num(bv, "byteStride", 0);
            if (stride == 0) stride = size;
            int off = (int)Json.Num(bv, "byteOffset", 0) + (int)Json.Num(a, "byteOffset", 0);
            var r = new int[count];
            for (int i = 0; i < count; i++)
            {
                int o = off + i * stride;
                r[i] = ctype == 5125 ? (int)BitConverter.ToUInt32(_bin, o) : ctype == 5123 ? BitConverter.ToUInt16(_bin, o) : _bin[o];
            }
            return r;
        }

        private Vector2[] Vec2(int? acc) { var f = Floats(acc.Value, out _); var r = new Vector2[f.Length / 2]; for (int i = 0; i < r.Length; i++) r[i] = new Vector2(f[i * 2], f[i * 2 + 1]); return r; }
        private Vector3[] Vec3(int? acc) { var f = Floats(acc.Value, out _); var r = new Vector3[f.Length / 3]; for (int i = 0; i < r.Length; i++) r[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]); return r; }
        private Vector4[] Vec4(int? acc) { var f = Floats(acc.Value, out _); var r = new Vector4[f.Length / 4]; for (int i = 0; i < r.Length; i++) r[i] = new Vector4(f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]); return r; }

        private Color[] Colors(int acc)
        {
            var f = Floats(acc, out int c);
            var r = new Color[f.Length / c];
            for (int i = 0; i < r.Length; i++) r[i] = new Color(f[i * c], f[i * c + 1], f[i * c + 2], c > 3 ? f[i * c + 3] : 1f);
            return r;
        }

        private static int Components(string type)
        {
            switch (type) { case "SCALAR": return 1; case "VEC2": return 2; case "VEC3": return 3; case "VEC4": return 4; case "MAT4": return 16; default: return 1; }
        }

        private static int ComponentSize(int ctype) => ctype == 5126 || ctype == 5125 ? 4 : ctype == 5123 || ctype == 5122 ? 2 : 1;

        private static float[] F(List<object> l) => l.Select(x => (float)Json.ToDouble(x)).ToArray();

        // ------------------------------------------------------------------ materials

        private static Shader _standard;

        /// A glTF material as a Standard material (what the game's vehicles use); a material whose name is
        /// "vanilla:<name>" (or with extras { "vanilla": "<name>" }) is the game's own material of that name.
        public Material MaterialOf(int index)
        {
            if (index < 0) return DefaultMaterial();
            if (_materials.TryGetValue(index, out var cached)) return cached;
            var m = (Dictionary<string, object>)Json.Arr(_json, "materials")[index];
            string name = Json.Str(m, "name", "material" + index);
            string vanilla = Json.Str(Json.Obj(m, "extras"), "vanilla") ?? (name.StartsWith("vanilla:") ? name.Substring(8) : null);
            if (vanilla != null)
            {
                var game = Vanilla.Material(vanilla);
                if (game != null) { _materials[index] = game; return game; }
                Problems.Add($"material '{name}': no game material '{vanilla}' (a plain one is used)");
            }

            if (_standard == null) _standard = Shader.Find("Standard");
            var mat = new Material(_standard) { name = name };
            Own(mat);
            var pbr = Json.Obj(m, "pbrMetallicRoughness");
            var baseColor = Json.Arr(pbr, "baseColorFactor");
            var colorF = baseColor != null ? F(baseColor) : new[] { 1f, 1f, 1f, 1f };
            mat.color = new Color(colorF[0], colorF[1], colorF[2], colorF[3]).gamma;  // glTF factors are linear, Standard's _Color is sRGB
            if (Json.Obj(pbr, "baseColorTexture") is Dictionary<string, object> bct) mat.mainTexture = Texture((int)Json.Num(bct, "index"), false);
            float metal = (float)Json.Num(pbr, "metallicFactor", 1), rough = (float)Json.Num(pbr, "roughnessFactor", 1);
            mat.SetFloat("_Metallic", metal);
            mat.SetFloat("_Glossiness", 1f - rough);
            if (Json.Obj(pbr, "metallicRoughnessTexture") is Dictionary<string, object> mrt)
            {
                var src = Texture((int)Json.Num(mrt, "index"), true);
                if (src != null)
                {
                    mat.SetTexture("_MetallicGlossMap", MetallicSmoothness(src, metal, rough));
                    mat.EnableKeyword("_METALLICGLOSSMAP");
                    mat.SetFloat("_GlossMapScale", 1f);
                }
            }
            if (Json.Obj(m, "normalTexture") is Dictionary<string, object> nt)
            {
                var tex = Texture((int)Json.Num(nt, "index"), true);
                if (tex != null)
                {
                    mat.SetTexture("_BumpMap", tex);
                    mat.SetFloat("_BumpScale", (float)Json.Num(nt, "scale", 1));
                    mat.EnableKeyword("_NORMALMAP");
                }
            }
            if (Json.Obj(m, "occlusionTexture") is Dictionary<string, object> ot)
            {
                var tex = Texture((int)Json.Num(ot, "index"), true);
                if (tex != null) { mat.SetTexture("_OcclusionMap", tex); mat.SetFloat("_OcclusionStrength", (float)Json.Num(ot, "strength", 1)); }
            }
            var emissive = Json.Arr(m, "emissiveFactor");
            if (emissive != null && F(emissive).Any(x => x > 0))
            {
                var e = F(emissive);
                mat.SetColor("_EmissionColor", new Color(e[0], e[1], e[2]));
                if (Json.Obj(m, "emissiveTexture") is Dictionary<string, object> et) mat.SetTexture("_EmissionMap", Texture((int)Json.Num(et, "index"), false));
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            string alpha = Json.Str(m, "alphaMode", "OPAQUE");
            if (alpha == "MASK") Cutout(mat, (float)Json.Num(m, "alphaCutoff", 0.5));
            else if (alpha == "BLEND") Transparent(mat);
            if (Json.Bool(m, "doubleSided")) mat.SetInt("_Cull", (int)CullMode.Off);
            _materials[index] = mat;
            return mat;
        }

        private Material _default;
        private Material DefaultMaterial()
        {
            if (_default != null) return _default;
            if (_standard == null) _standard = Shader.Find("Standard");
            _default = new Material(_standard) { name = "default" };
            Own(_default);
            return _default;
        }

        private static void Cutout(Material m, float cutoff)
        {
            m.SetFloat("_Mode", 1); m.SetFloat("_Cutoff", cutoff);
            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.Zero); m.SetInt("_ZWrite", 1);
            m.EnableKeyword("_ALPHATEST_ON"); m.DisableKeyword("_ALPHABLEND_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)RenderQueue.AlphaTest;
        }

        private static void Transparent(Material m)
        {
            m.SetFloat("_Mode", 3);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.One); m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha); m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON"); m.DisableKeyword("_ALPHABLEND_ON"); m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        /// A glTF image as a texture (linear for data: normal, metal/roughness and occlusion maps).
        private Texture2D Texture(int textureIndex, bool linear)
        {
            var textures = Json.Arr(_json, "textures");
            if (textures == null || textureIndex < 0 || textureIndex >= textures.Count) return null;
            int image = (int)Json.Num(textures[textureIndex], "source", -1);
            string key = image + (linear ? ":lin" : ":srgb");
            if (_textures.TryGetValue(key, out var cached)) return cached;
            var img = (Dictionary<string, object>)Json.Arr(_json, "images")[image];
            byte[] bytes = null;
            if (img.ContainsKey("bufferView"))
            {
                var bv = (Dictionary<string, object>)Json.Arr(_json, "bufferViews")[(int)Json.Num(img, "bufferView")];
                int off = (int)Json.Num(bv, "byteOffset", 0), len = (int)Json.Num(bv, "byteLength");
                bytes = new byte[len];
                Buffer.BlockCopy(_bin, off, bytes, 0, len);
            }
            else if (Json.Str(img, "uri") is string uri)
            {
                if (uri.StartsWith("data:")) bytes = Convert.FromBase64String(uri.Substring(uri.IndexOf(',') + 1));
                else
                {
                    // a picture file next to the model (textures/, saved once for every model that uses it): loaded once too
                    string f = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", Uri.UnescapeDataString(uri)));
                    if (!File.Exists(f)) { Problems.Add($"image '{uri}': no such file"); return null; }
                    string shared = f + "|" + File.GetLastWriteTimeUtc(f).Ticks + (linear ? "|lin" : "|srgb");
                    if (Shared.TryGetValue(shared, out var s) && s.tex != null)
                    {
                        Shared[shared] = (s.tex, s.users + 1);
                        _shared.Add(shared);
                        _textures[key] = s.tex;
                        return s.tex;
                    }
                    var t = Decode(File.ReadAllBytes(f), Json.Str(img, "name", System.IO.Path.GetFileNameWithoutExtension(f)), linear);
                    if (t == null) return null;
                    Ours.Add(t);
                    Shared[shared] = (t, 1);
                    _shared.Add(shared);
                    _textures[key] = t;
                    return t;
                }
            }
            if (bytes == null) { Problems.Add($"image {image}: no data"); return null; }
            var tex = Decode(bytes, Json.Str(img, "name", "image" + image), linear);
            if (tex == null) return null;
            Own(tex);
            _textures[key] = tex;
            return tex;
        }

        /// Pictures from files, shared by every model that uses them (path|time|colour space -> texture, models using it).
        private static readonly Dictionary<string, (Texture2D tex, int users)> Shared = new Dictionary<string, (Texture2D, int)>();
        private readonly HashSet<string> _shared = new HashSet<string>();

        private Texture2D Decode(byte[] bytes, string name, bool linear)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
            if (!ImageConversion.LoadImage(tex, bytes, false)) { Problems.Add($"image '{name}': not a PNG or JPEG"); Object.Destroy(tex); return null; }
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.anisoLevel = 4;
            return tex;
        }

        /// glTF keeps metal in B and roughness in G; Standard wants metal in R and smoothness in A.
        private Texture2D MetallicSmoothness(Texture2D src, float metalF, float roughF)
        {
            var px = src.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                byte metal = (byte)Mathf.Clamp(c.b * metalF, 0, 255);
                byte smooth = (byte)Mathf.Clamp(255 - c.g * roughF, 0, 255);
                px[i] = new Color32(metal, metal, metal, smooth);
            }
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true, true) { name = src.name + " (metal/smooth)" };
            tex.SetPixels32(px);
            tex.Apply(true, false);
            Own(tex);
            return tex;
        }

        public void Unload()
        {
            foreach (var o in Created) { Ours.Remove(o); if (o != null) Object.Destroy(o); }
            Created.Clear();
            foreach (var k in _shared)
            {
                if (!Shared.TryGetValue(k, out var s)) continue;
                if (s.users > 1) { Shared[k] = (s.tex, s.users - 1); continue; }
                Shared.Remove(k);
                if (s.tex != null) { Ours.Remove(s.tex); Object.Destroy(s.tex); }
            }
            _shared.Clear();
        }
    }
}
