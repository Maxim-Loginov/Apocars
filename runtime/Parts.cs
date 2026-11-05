using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Builds a car part from scratch out of the vanilla part of its kind: the part's FSMs (its ID, fitting and
    /// unfitting, opening, condition, saving...), Rigidbody and grab collider, every vanilla mesh and collider left out,
    /// then the asset's model and colliders. Its ID decides what it fits (a "door" fits any door hinge, the game's or ours).
    internal static class Parts
    {
        /// Part ID -> the vanilla part it is made from unless the asset names one ("template").
        public static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>
        {
            ["door"] = "door_car_1_L", ["hood"] = "hood_1", ["trunk"] = "trunk_1", ["bumper"] = "bumper_1", ["seat"] = "poloska_seat_front",
            ["steeringwheel"] = "steeringwheel_1", ["gearlever"] = "gearlever_1", ["radio"] = "radio_car", ["windshield"] = "windshield_1_glass",
            ["roofrack"] = "roofrack_small", ["exhaust"] = "exhaust_single", ["wheel"] = "wheel_2", ["radiator"] = "radiator_medium",
            ["headlight"] = "Headlight", ["attachable"] = "metal_plate_1", ["engine"] = "1.2L I4 59HP 87Nm Gasoline",
        };

        public static readonly Dictionary<string, GameObject> Templates = new Dictionary<string, GameObject>();

        /// Its weight (spec mass, kg): the template's body, and the body the game gives it again each time it comes off a
        /// car (CheckTag: AddComponent Rigidbody, SetProperty mass). A wheel's mass on the car is scaled along with it.
        private static void SetMass(Dictionary<string, object> root, Dictionary<string, object> spec, string type)
        {
            float mass = (float)Json.Num(spec, "mass", 0);
            if (mass <= 0) return;
            mass = Mathf.Clamp(mass, 0.1f, 100000f);
            float was = 0f;   // the game part's own weight off the car: CheckTag's, else its body's
            foreach (var c in Json.Arr(root, "components") ?? new List<object>())
                if ((Json.Str(c, "$type") ?? "").StartsWith("UnityEngine.Rigidbody,") && Json.Obj(c, "props") is Dictionary<string, object> props)
                {
                    was = (float)Json.Num(props, "mass", 0);
                    props["mass"] = (double)mass;
                }
            var sets = new[] { "CheckTag", "Condition" }.SelectMany(n => FsmEdit.Actions(root, n)).Where(a => a.Type == "SetProperty")
                .Select(a => (a, prop: a.Var("targetProperty"))).Select(x => (x.a, x.prop, f: Json.Obj(x.prop, "FloatParameter")))
                .Where(x => x.f != null && !Json.Bool(x.f, "useVariable")).ToList();
            foreach (var x in sets.Where(x => x.a.FsmName == "CheckTag" && Json.Str(x.prop, "PropertyName") == "mass"))
            {
                was = (float)Json.Num(x.f, "value", was);
                x.f["value"] = (double)mass;
            }
            // a wheel: its mass on the car (the WheelController's, lighter than the part: 33 kg for a 50 kg street
            // wheel) grows and shrinks with its weight, so a wheel at the game's weight drives as the game's does
            if (type == "wheel" && was > 0)
                foreach (var x in sets.Where(x => x.a.FsmName == "Condition" && Json.Str(x.prop, "PropertyName") == "Mass"))
                    x.f["value"] = Json.Num(x.f, "value", 0) * mass / was;
        }

        /// A wheel as the game's wheels are: fitted, its Condition FSM gives the car's wheel its radius, width, mass and
        /// grip (worn out, below 1% condition: a flat, 0.6 of the radius and grip 0.2), and its name shows its grip
        /// ("Street Wheel / Grip: 0.9"). Radius and width are measured from the model (the game's: 96% of half the tyre's
        /// height, a little squashed, and 95% of its width); grip is the asset's (spec grip), or the game wheel's.
        private static void SetWheel(Dictionary<string, object> root, Dictionary<string, object> spec, GameObject model, string name, Assembler.Result res)
        {
            var hidden = new HashSet<string>(Spec.Entries(spec, "boxes")
                .Where(b => Json.Str(b, "kind") == "solid" && Json.Bool(b, "hidden", true)).Select(b => Json.Str(b, "node") ?? ""));
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            var toModel = model.transform.worldToLocalMatrix;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || hidden.Contains(mf.name)) continue;
                var m = toModel * mf.transform.localToWorldMatrix;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            float? radius = null, width = null;
            if (!float.IsInfinity(min.x))
            {
                // the axle is along its thinnest side; the tyre's height is the larger of the other two
                var size = max - min;
                var sides = new[] { size.x, size.y, size.z }.OrderBy(v => v).ToArray();
                width = 0.95f * sides[0];
                radius = 0.96f * sides[2] / 2f;
            }
            else res.Problems.Add("wheel: no mesh to measure (the game wheel's radius is used)");
            float? grip = spec.ContainsKey("grip") ? Mathf.Clamp((float)Json.Num(spec, "grip", 0.9), 0.05f, 5f) : (float?)null;
            float shownGrip = grip ?? 0.9f;
            foreach (var act in FsmEdit.Actions(root, "Condition").Where(a => a.Type == "SetProperty"))
            {
                var prop = act.Var("targetProperty");
                var f = Json.Obj(prop, "FloatParameter");
                if (f == null || Json.Bool(f, "useVariable")) continue;
                bool flat = act.State == "broken";
                switch (Json.Str(prop, "PropertyName"))
                {
                    case "Radius": if (radius != null) f["value"] = (double)(flat ? radius.Value * 0.6f : radius.Value); break;
                    case "Width": if (width != null && !flat) f["value"] = (double)width.Value; break;
                    case "forwardFriction.grip":
                        if (flat) break;
                        if (grip != null) f["value"] = (double)grip.Value; else shownGrip = (float)Json.Num(f, "value", 0.9);
                        break;
                }
            }
            foreach (var act in FsmEdit.Actions(root, "ItemName").Where(a => a.Type == "UiTextSetText"))
            {
                var text = act.Var("text");
                if (text != null && (Json.Str(text, "value") ?? "").Contains("Grip:"))
                    text["value"] = $"{name} / Grip: {Math.Round(shownGrip, 2)}";
            }
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"Wheel {name}: radius {radius}, width {width}, grip {shownGrip}");
        }

        public static Assembler.Result Build(string id, Dictionary<string, object> spec, Gltf glb, Transform holder)
        {
            var res = new Assembler.Result();
            string type = Json.Str(spec, "type", "attachable");
            string tplName = Json.Str(spec, "template") ?? (Defaults.TryGetValue(type, out var d) ? d : null);
            var doc = tplName != null ? Vanilla.Doc(tplName) : null;
            if (doc == null) { res.Problems.Add($"no vanilla part '{tplName}' to build a '{type}' from"); return res; }

            var copy = (Dictionary<string, object>)Json.Clone(doc);
            var root = Json.Obj(copy, "root");
            // the vanilla part's looks and shape go; its workings stay
            StripAll(root, "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer");
            Strip(root, "UnityEngine.BoxCollider", "UnityEngine.MeshCollider", "UnityEngine.CapsuleCollider");
            var colliders = Json.Arr(root, "children")?.OfType<Dictionary<string, object>>().FirstOrDefault(c => Json.Str(c, "name") == "colliders");
            Json.Arr(colliders, "children")?.Clear();
            Json.Arr(root, "children")?.RemoveAll(c => Json.Str(c, "name") == "model");

            // what it's called, its ID, which way it opens
            if (FsmEdit.Variable(root, "ItemName", "stringVariables", "ItemName") is Dictionary<string, object> nameVar)
                nameVar["value"] = Json.Str(spec, "name", id);
            if (FsmEdit.Variable(root, "ID", "stringVariables", "ID") is Dictionary<string, object> idVar)
                idVar["value"] = type;
            if (spec.TryGetValue("open", out var open) && open is List<object>)
            {
                var angle = Spec.Vec(open, Vector3.zero);
                foreach (var act in FsmEdit.Actions(root, "DoorOpen"))
                    if (act.Type == "iTweenRotateTo") act.SetVector3("vectorRotation", angle);
            }

            // held, a part doesn't bump into anything: the player's hand sends it CollidersDisable, and its Colliders
            // FSM switches its "colliders" child (every solid of ours is there) off until it's dropped, thrown or
            // fitted. A part made like a game part without that FSM (bumpers, wheels) gets the doors' one
            EnsureCollidersFsm(root, res);

            // the model, under "model" as the game's parts have it (built first: a wheel is measured for its FSMs)
            var model = glb.Build(holder, "model");
            if (type == "wheel") SetWheel(root, spec, model, Json.Str(spec, "name", id), res);
            SetMass(root, spec, type);

            var rb = new Rebuild();
            var src = rb.Load(tplName, copy);
            var go = rb.Create(src, root, holder);
            rb.Apply();
            res.Problems.AddRange(rb.Problems);
            go.name = id;
            model.transform.SetParent(go.transform, false);
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
            var solids = Spec.Entries(spec, "boxes").Where(b => Json.Str(b, "kind") == "solid").ToList();
            var colT = go.transform.Find("colliders");
            if (colT == null) { colT = new GameObject("colliders").transform; colT.SetParent(go.transform, false); }
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = go.transform.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                }
            }
            foreach (var s in solids)
            {
                var n = glb.Find(Json.Str(s, "node") ?? "");
                if (n?.Object == null) { res.Problems.Add($"solid: no object '{Json.Str(s, "node")}'"); continue; }
                var t = n.Object.transform;
                var mf = t.GetComponent<MeshFilter>();
                var c = new GameObject("collider") { layer = 8 };
                c.transform.SetParent(colT, false);
                c.transform.position = t.position;
                c.transform.rotation = t.rotation;
                if (Json.Bool(s, "mesh") && mf != null && mf.sharedMesh != null)
                {
                    // the mesh itself (blown up a little, or a convex piece of a split) as a convex collider
                    c.transform.localScale = t.lossyScale;
                    var mc = c.AddComponent<MeshCollider>();
                    mc.convex = true;
                    mc.sharedMesh = Assembler.Inflated(mf.sharedMesh, (float)Json.Num(s, "inflate", 0.02));
                }
                else
                {
                    var bc = c.AddComponent<BoxCollider>();
                    if (mf != null && mf.sharedMesh != null) { bc.center = Vector3.Scale(mf.sharedMesh.bounds.center, t.lossyScale); bc.size = Vector3.Scale(mf.sharedMesh.bounds.size, t.lossyScale); }
                    else bc.size = new Vector3(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z)) * 2f;
                }
                // a collision-only object isn't drawn; the part's own mesh used as its collider is. At once: copies of
                // the template are made in this same frame (hot export respawns them), Destroy would come too late
                if (mf != null && Json.Bool(s, "hidden", true)) { Object.DestroyImmediate(t.GetComponent<MeshRenderer>()); Object.DestroyImmediate(mf); }
            }

            // the physics lock: loose things inside these boxes ride along with the part (as the game's roof rack has it)
            var locks = Spec.Entries(spec, "boxes").Where(b => Json.Str(b, "kind") == "physlock").ToList();
            if (locks.Count > 0)
            {
                var lockT = go.transform.Find("PhysicsLock");
                if (lockT == null)
                {
                    lockT = new GameObject("PhysicsLock") { layer = 18 }.transform;
                    lockT.SetParent(go.transform, false);
                }
                foreach (var bc in lockT.GetComponents<BoxCollider>()) Object.DestroyImmediate(bc);
                int li = 0;
                foreach (var b in locks)
                {
                    var n = glb.Find(Json.Str(b, "node") ?? "");
                    if (n?.Object == null) { res.Problems.Add($"physics lock: no object '{Json.Str(b, "node")}'"); continue; }
                    var t = n.Object.transform;
                    var l = new GameObject("lock_" + li++) { layer = 18 };
                    l.transform.SetParent(lockT, false);
                    l.transform.position = t.position;
                    l.transform.rotation = t.rotation;
                    var bc = l.AddComponent<BoxCollider>();
                    bc.isTrigger = true;
                    var mf = t.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) { bc.center = Vector3.Scale(mf.sharedMesh.bounds.center, t.lossyScale); bc.size = Vector3.Scale(mf.sharedMesh.bounds.size, t.lossyScale); Object.DestroyImmediate(t.GetComponent<MeshRenderer>()); Object.DestroyImmediate(mf); }
                    else bc.size = new Vector3(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z)) * 2f;
                }
            }
            // loose, it bumps and is picked up by its overall box (as the game's parts are)
            var whole = go.AddComponent<BoxCollider>();
            whole.isTrigger = true;    // what the player looks at to pick it up, as on the game's doors: it never bumps
            whole.center = bounds.center;
            whole.size = any ? bounds.size : Vector3.one * 0.3f;
            if (solids.Count == 0 && any)
            {
                var c = new GameObject("collider") { layer = 8 };
                c.transform.SetParent(colT, false);
                var bc = c.AddComponent<BoxCollider>();
                bc.center = bounds.center; bc.size = bounds.size;
            }

            // on any car's hinge it hangs by the edge that hinge expects and opens the right way
            var fit = Hinges.FromSpec(spec.TryGetValue("fit", out var f) ? f : true, type);
            if (fit != null) go.AddComponent<HingedPart>().swing = fit.Swing.ToString().ToLowerInvariant();

            float mass = (float)Json.Num(spec, "mass", 0);
            if (mass > 0 && go.GetComponent<Rigidbody>() is Rigidbody body) body.mass = mass;
            res.Root = go;
            return res;
        }

        private const string CollidersFrom = "door_car_1_L";

        private static void EnsureCollidersFsm(Dictionary<string, object> root, Assembler.Result res)
        {
            var comps = Json.Arr(root, "components");
            if (comps == null) return;
            bool has = comps.OfType<Dictionary<string, object>>().Any(c => Json.Str(Json.Obj(Json.Obj(c, "fields"), "fsm"), "name") == "Colliders");
            if (has) return;
            var donor = Vanilla.Doc(CollidersFrom);
            var fsm = Json.Arr(Json.Obj(donor, "root"), "components")?.OfType<Dictionary<string, object>>()
                          .FirstOrDefault(c => Json.Str(Json.Obj(Json.Obj(c, "fields"), "fsm"), "name") == "Colliders");
            if (fsm == null) { res.Problems.Add($"no Colliders FSM to borrow from {CollidersFrom}: the part stays solid when held"); return; }
            comps.Add(Json.Clone(fsm));
        }

        private static void Strip(Dictionary<string, object> node, params string[] types) =>
            Json.Arr(node, "components")?.RemoveAll(c => types.Any(t => (Json.Str(c, "$type") ?? "").StartsWith(t + ",")));

        private static void StripAll(Dictionary<string, object> node, params string[] types)
        {
            if (node == null) return;
            Strip(node, types);
            foreach (var c in Json.Arr(node, "children") ?? new List<object>()) StripAll(c as Dictionary<string, object>, types);
        }

        /// A built part joins the game's item list (what the game saves loose and fitted items by).
        public static void Registered(Library.Asset a, GameObject template)
        {
            Templates.TryGetValue(a.Id, out var previous);
            Templates[a.Id] = template;
            foreach (var proxy in Object.FindObjectsOfType<PlayMakerArrayListProxy>())
            {
                if (proxy.referenceName != "ItemList" || proxy.arrayList == null) continue;
                for (int i = proxy.arrayList.Count - 1; i >= 0; i--)   // an earlier build of it, replaced by this one
                    if (proxy.arrayList[i] is GameObject g && (g == null || (previous != null && g == previous && g != template))) proxy.arrayList.RemoveAt(i);
                if (!proxy.arrayList.Contains(template)) proxy.arrayList.Add(template);
            }
        }
    }
}
