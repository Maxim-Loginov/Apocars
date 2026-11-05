using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Apocars
{
    /// Builds a vehicle from scratch out of the vanilla contract: the game's own car skeleton (root FSMs, NWH vehicle,
    /// engine sound, the drive trigger with its input and cameras, ignition, light switch, fuel system, physics lock,
    /// damage zone, trailer hitch...), with every vanilla body mesh and collider left out, then the asset's body,
    /// colliders and a hinge for every attachment point, each made from the game's hinge of that kind and moved where
    /// the asset puts it (positions inside its FSMs moved with it).
    internal static class Assembler
    {
        public const string SkeletonCar = "Poloska";

        /// Kinds of attachment point -> the vanilla hinge(s) they are made from (car, hinge object).
        private static readonly Dictionary<string, (string car, string[] hinges)> Templates = new Dictionary<string, (string, string[])>
        {
            ["engine"] = ("Junker", new[] { "hinge_engine" }),      // front-engined: the Poloska's faces backwards
            ["radiator"] = (SkeletonCar, new[] { "hinge_radiator" }),
            ["exhaust"] = ("Junker", new[] { "hinge_exhaust" }),
            ["wheel"] = (SkeletonCar, new[] { "hinge_wheel_FL", "hinge_wheel_FR", "hinge_wheel_RL", "hinge_wheel_RR" }),
            ["gearlever"] = (SkeletonCar, new[] { "hinge_gearlever_Joint" }),
            ["steeringwheel"] = (SkeletonCar, new[] { "hinge_steeringwheel" }),
            ["radio"] = (SkeletonCar, new[] { "hinge_radio" }),
            ["seat"] = (SkeletonCar, new[] { "hinge_seat_driver", "hinge_seat_passenger", "hinge_seat_rear" }),
            ["door"] = (SkeletonCar, new[] { "hinge_door_L", "hinge_door_R" }),
            ["trunk"] = ("Junker", new[] { "hinge_trunk" }),
            ["hood"] = (SkeletonCar, new[] { "hinge_hood" }),
            ["bumper"] = (SkeletonCar, new[] { "hinge_bumper_front", "hinge_bumper_rear" }),
            ["window"] = (SkeletonCar, new[] { "hinge_windshield" }),
            ["roofrack"] = (SkeletonCar, new[] { "hinge_roofrack" }),
            ["headlight"] = (SkeletonCar, new[] { "hinge_headlight" }),
        };
        public static IEnumerable<string> Kinds => Templates.Keys.Concat(Controls.Keys);

        /// Kinds that are no hinge but one of the skeleton car's own objects, put where the asset says -> that object.
        private static readonly Dictionary<string, string> Controls = new Dictionary<string, string>
        {
            ["switchpanel"] = "switch_panel",       // ignition, starter, fuel pump, fan, lights, horn
            ["fuelcap"] = "Fuel",                    // the filler: fuel level and use, cans, syphon
            ["fueltank"] = "parts/fueltank",         // to look at
            ["handbrake"] = "handbrake",             // pulled and released by clicking it (or the handbrake key)
            ["toolbox"] = "parts/toolset_1",         // the toolset (repairs): used with F
            ["adjuster"] = "parts/toolset_2",        // the part adjuster toolset: used with F
            ["suspension"] = "hinge_suspension_parent", // the lift kit's slot: fitted, the lifted suspension shows
        };
        private const string SwitchPanel = "switchpanel";

        /// Sitting, the player's camera is this far above sitPos, along its up (the player's InCar FSM puts PCameraPos
        /// 0.25 up; on foot it's 0.75).
        public const float EyeHeight = 0.25f;

        /// What of the vanilla car's own objects the skeleton keeps (the rest is its bodywork, colliders and hinges).
        private static readonly HashSet<string> KeepTop = new HashSet<string>
        {
            "RSound", "hitch_raycast", "hitch", "SoundCrash", "Colliders", "DriveTrigger", "START", "Fuel", "Dashboard", "handbrake",
            "switch_panel", "suspension_model", "windshield_model", "parts", "CarAttack", "hinge_suspension_parent",
        };
        private static readonly HashSet<string> KeepInParts = new HashSet<string> { "hingeAdjustVisibility", "PhysicsLock", "PlayerModel_Sit", "pedals" };

        private sealed class Marker
        {
            public string Node;
            public Vector3 Pos;           // a point's position; a box's centre
            public Vector3 Origin;        // the object's own origin (where its mesh is drawn from)
            public Quaternion Rot = Quaternion.identity;
            public Vector3 Size;          // for boxes
            public Mesh Mesh;             // the node's mesh, if it has one
            public Vector3 Scale = Vector3.one;
        }

        public sealed class Result
        {
            public GameObject Root;
            public readonly List<string> Problems = new List<string>();
        }

        // ------------------------------------------------------------------ the build

        public static Result Build(string id, Dictionary<string, object> spec, Gltf glb, Transform holder)
        {
            var res = new Result();
            var skeletonDoc = Vanilla.Doc(SkeletonCar);
            if (skeletonDoc == null) { res.Problems.Add($"no vanilla {SkeletonCar} (load a game, or run Tools.DumpVanilla once)"); return res; }

            // the asset's model, built where the car's root will be: markers are read relative to it
            var model = glb.Build(holder, id + " (model)");
            var markers = new Dictionary<string, Marker>();
            foreach (var n in glb.Nodes) if (n.Object != null) markers[n.Name] = Measure(model.transform, n.Object.transform);
            Marker M(string name, string what)
            {
                if (name != null && markers.TryGetValue(name, out var m)) return m;
                if (name != null) res.Problems.Add($"{what}: no object '{name}' in the model");
                return null;
            }

            var attach = Spec.Entries(spec, "attach");
            var boxes = Spec.Entries(spec, "boxes");

            // ---- the skeleton, pruned
            var skel = (Dictionary<string, object>)Json.Clone(skeletonDoc);
            var root0 = Json.Obj(skel, "root");
            var vanillaRoot = Index(Json.Obj(skeletonDoc, "root"));
            Prune(root0, attach);
            WirePowertrain(root0, spec, attach, markers, res);
            SetFuelCapacity(Child(root0, "Fuel"), spec, res);

            var rb = new Rebuild();
            Transform car = null;
            rb.Named = path => car == null ? null : path.Length == 0 ? car : car.Find(path);
            rb.Redirect = (src, path) =>
            {
                if (car == null) return null;
                string names = src.NamePath(path);
                return names.Length == 0 ? car : car.Find(names);
            };
            var skelSrc = rb.Load(SkeletonCar, skeletonDoc);   // indexed unpruned: references to what was left out still have names
            var rootGo = rb.Create(skelSrc, root0, holder);
            car = rootGo.transform;
            rootGo.name = id;

            // ---- hinges
            var used = new HashSet<string>();
            var hingeOf = new Dictionary<Dictionary<string, object>, Transform>();
            foreach (var a in attach)
            {
                string kind = Json.Str(a, "kind");
                var m = M(Json.Str(a, "node"), $"attachment '{kind}'");
                if (m == null) continue;
                if (kind != null && Controls.ContainsKey(kind)) continue;   // the skeleton's own, see PlaceSkeleton
                if (kind == null || !Templates.TryGetValue(kind, out var tpl)) { res.Problems.Add($"attachment '{Json.Str(a, "node")}': unknown kind '{kind}'"); continue; }
                var doc = Vanilla.Doc(tpl.car);
                if (doc == null) { res.Problems.Add($"{kind}: no vanilla {tpl.car}"); continue; }
                string hingeName = Pick(kind, tpl.hinges, m.Pos, a, attach, markers);
                var src0 = Index(Json.Obj(doc, "root")).Values.FirstOrDefault(n => Json.Str(n, "name") == hingeName && (Json.Str(n, "path") ?? "").IndexOf('/') < 0);
                if (src0 == null) { res.Problems.Add($"{kind}: no {hingeName} on the vanilla {tpl.car}"); continue; }
                var node = (Dictionary<string, object>)Json.Clone(src0);
                if (kind == "wheel") SetSuspension(node, a);
                string name = Name(kind, hingeName, m.Pos, used);
                PlaceHinge(node, kind, name, m, a, res);
                if (kind == "wheel") SetLiftStates(node, a, res);
                var src = rb.Load(tpl.car + "/" + name, doc);
                var go = rb.Create(src, node, car);
                go.name = name;
                hingeOf[a] = go.transform;
                // doors, hoods and trunk lids hang by their hinge edge and open the right way, whatever is fitted
                var fit = Hinges.FromSpec(a.TryGetValue("fit", out var f) ? f : true, kind);
                if (fit != null) go.AddComponent<HingeSlot>().Use(fit);
            }

            rb.Apply();
            res.Problems.AddRange(rb.Problems);

            // ---- the body: the model's meshes (all but the markers and collider-only objects, unless listed)
            var bodyNames = Json.Arr(spec, "body")?.Select(x => x as string).Where(x => x != null).ToList();
            var panelParts = CustomPanel(glb, attach);   // a panel of the asset's own: drawn as the switch panel, not as body
            var body = new GameObject("body");
            body.transform.SetParent(car, false);
            body.layer = 8;
            var liftKit = attach.FirstOrDefault(x => Json.Str(x, "kind") == "suspension");
            var suspNames = new Dictionary<string, string>();   // node -> "stock" / "lifted"
            if (liftKit != null)
                foreach (var v in new[] { "stock", "lifted" })
                    if (Json.Str(liftKit, v + "_mesh") is string sn && sn.Length > 0) suspNames[sn] = v;
            var suspParts = new List<(Transform t, string variant)>();
            foreach (var n in glb.Nodes)
            {
                if (n.Object == null || n.Mesh < 0) continue;
                if (panelParts.ContainsKey(n.Name)) continue;
                bool listed = bodyNames != null
                    ? bodyNames.Contains(n.Name) || AncestorListed(n, bodyNames)
                    : !boxes.Any(b => Json.Str(b, "node") == n.Name && (Json.Str(b, "kind") != "solid" || Json.Bool(b, "hidden")));
                if (!listed) continue;
                var copy = new GameObject(n.Name);
                copy.layer = 8;
                copy.transform.SetParent(body.transform, false);
                for (var up = n; up != null; up = up.Parent)
                    if (suspNames.TryGetValue(up.Name, out var variant)) { suspParts.Add((copy.transform, variant)); break; }
                var mk = markers[n.Name];
                copy.transform.localPosition = mk.Origin;
                copy.transform.localRotation = mk.Rot;
                copy.transform.localScale = mk.Scale;
                var mf = n.Object.GetComponent<MeshFilter>();
                var mr = n.Object.GetComponent<MeshRenderer>();
                if (mf == null || mr == null) continue;
                copy.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = copy.AddComponent<MeshRenderer>();
                r.sharedMaterials = mr.sharedMaterials;
            }
            var bounds = BodyBounds(car, body.transform);
            LiftKitMeshes(car, suspParts, res);

            // ---- colliders, functional boxes and the skeleton's parts put in place
            Solids(car, boxes, markers, res);
            PlaceSkeleton(car, vanillaRoot, attach, boxes, markers, hingeOf, bounds, res);
            FitCustomPanel(car, panelParts, glb, markers, boxes, res);

            float mass = (float)Json.Num(spec, "mass", 0);
            float vanillaMass = car.GetComponent<Rigidbody>() is Rigidbody rb0 ? rb0.mass : 600f;   // the skeleton car's, its inertia is for
            if (mass > 0 && car.GetComponent<Rigidbody>() is Rigidbody body0) body0.mass = mass;
            MassProperties(car, boxes, markers, bounds, vanillaMass, res);

            Object.Destroy(model);
            res.Root = rootGo;
            return res;
        }

        /// The asset's stock and lifted suspension meshes into the car's suspension_model (its first child is the stock
        /// suspension, its second the lifted one, "lifted" or "buggy"): the car's checkSuspension FSM shows one and
        /// hides the other as the lift kit comes off and goes on.
        private static void LiftKitMeshes(Transform car, List<(Transform t, string variant)> parts, Result res)
        {
            if (parts.Count == 0) return;
            var model = car.Find("suspension_model");
            if (model == null || model.childCount < 2) { res.Problems.Add("lifted suspension: the skeleton car has no suspension_model to put the meshes in"); return; }
            var stock = model.Find("stock") ?? model.GetChild(0);
            var lifted = model.Find("lifted") ?? model.Find("buggy") ?? model.GetChild(1);
            foreach (var (t, variant) in parts) t.SetParent(variant == "lifted" ? lifted : stock, true);
        }

        private static bool AncestorListed(Gltf.Node n, List<string> names)
        {
            for (var p = n.Parent; p != null; p = p.Parent) if (names.Contains(p.Name)) return true;
            return false;
        }

        private static Marker Measure(Transform root, Transform t)
        {
            var m = new Marker
            {
                Node = t.name,
                Pos = root.InverseTransformPoint(t.position),
                Origin = root.InverseTransformPoint(t.position),
                Rot = Quaternion.Inverse(root.rotation) * t.rotation,
                Scale = t.lossyScale,
            };
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                m.Mesh = mf.sharedMesh;
                var b = mf.sharedMesh.bounds;
                m.Pos = root.InverseTransformPoint(t.TransformPoint(b.center));
                m.Size = Vector3.Scale(b.size, Abs(t.lossyScale));
            }
            else m.Size = Abs(t.lossyScale) * 2f;   // a Blender cube empty spans -1..1
            return m;
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static Dictionary<string, Dictionary<string, object>> Index(Dictionary<string, object> root)
        {
            var d = new Dictionary<string, Dictionary<string, object>>();
            void Walk(Dictionary<string, object> n)
            {
                if (n == null) return;
                d[Json.Str(n, "path", "")] = n;
                foreach (var c in Json.Arr(n, "children") ?? new List<object>()) Walk(c as Dictionary<string, object>);
            }
            Walk(root);
            return d;
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> n, string name) =>
            (Json.Arr(n, "children") ?? new List<object>()).OfType<Dictionary<string, object>>().FirstOrDefault(c => Json.Str(c, "name") == name);

        private static Dictionary<string, object> Find(Dictionary<string, object> n, string path)
        {
            foreach (var part in path.Split('/')) { n = n == null ? null : Child(n, part); }
            return n;
        }

        // ------------------------------------------------------------------ the skeleton

        private static void Prune(Dictionary<string, object> root, List<Dictionary<string, object>> attach)
        {
            bool panel = attach.Any(a => Json.Str(a, "kind") == SwitchPanel);
            bool cap = attach.Any(a => Json.Str(a, "kind") == "fuelcap");
            bool tank = attach.Any(a => Json.Str(a, "kind") == "fueltank");
            bool handbrake = attach.Any(a => Json.Str(a, "kind") == "handbrake");
            bool toolbox = attach.Any(a => Json.Str(a, "kind") == "toolbox");
            bool adjuster = attach.Any(a => Json.Str(a, "kind") == "adjuster");
            bool column = attach.Any(a => Json.Str(a, "kind") == "steeringwheel" && Json.Str(a, "column") != null);
            Strip(root, "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer", "UnityEngine.BoxCollider");
            var kids = Json.Arr(root, "children");
            kids.RemoveAll(c => !KeepTop.Contains(Json.Str(c, "name")));
            Json.Arr(Child(root, "Colliders"), "children")?.Clear();
            Json.Arr(Child(root, "parts"), "children")?.RemoveAll(c => !KeepInParts.Contains(Json.Str(c, "name")) && !(column && Json.Str(c, "name") == "steering rod") && !(tank && Json.Str(c, "name") == "fueltank")
                && !(toolbox && Json.Str(c, "name") == "toolset_1") && !(adjuster && Json.Str(c, "name") == "toolset_2"));
            if (!handbrake) StripAll(Child(root, "handbrake"), "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer", "UnityEngine.CapsuleCollider");
            if (!panel) StripAll(Child(root, "switch_panel"), "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer");   // works, unseen
            StripAll(Child(root, "suspension_model"), "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer");
            StripAll(Child(root, "windshield_model"), "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer", "UnityEngine.BoxCollider");
            StripAll(Find(root, "parts/pedals"), "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer");
            if (!cap) StripAll(Find(root, "Fuel/mesh"), "UnityEngine.MeshFilter", "UnityEngine.MeshRenderer");
        }

        private static void Strip(Dictionary<string, object> node, params string[] types) =>
            Json.Arr(node, "components")?.RemoveAll(c => types.Any(t => (Json.Str(c, "$type") ?? "").StartsWith(t + ",")));

        private static void StripAll(Dictionary<string, object> node, params string[] types)
        {
            if (node == null) return;
            Strip(node, types);
            foreach (var c in Json.Arr(node, "children") ?? new List<object>()) StripAll(c as Dictionary<string, object>, types);
        }

        // ------------------------------------------------------------------ hinges

        /// Which vanilla hinge of a kind an attachment point is made from: by side and end of the car.
        private static string Pick(string kind, string[] hinges, Vector3 pos, Dictionary<string, object> a, List<Dictionary<string, object>> all,
                                   Dictionary<string, Marker> markers)
        {
            bool left = pos.x < 0;
            var same = all.Where(x => Json.Str(x, "kind") == kind).Select(x => markers.TryGetValue(Json.Str(x, "node") ?? "", out var mk) ? (Vector3?)mk.Pos : null)
                          .Where(p => p != null).Select(p => p.Value.z).ToList();
            float mid = kind == "wheel" && same.Count > 1 ? same.Average() : 0f;   // wheels: their own middle; bumpers: the model's origin
            bool front = pos.z >= mid;
            switch (kind)
            {
                case "wheel": return hinges[(front ? 0 : 2) + (left ? 0 : 1)];
                case "door": return left ? hinges[0] : hinges[1];
                case "bumper": return front ? hinges[0] : hinges[1];
                case "seat":
                {
                    var seats = all.Where(x => Json.Str(x, "kind") == "seat").Select(x => markers.TryGetValue(Json.Str(x, "node") ?? "", out var mk) ? mk.Pos : Vector3.zero).ToList();
                    float frontMost = seats.Count > 0 ? seats.Max(p => p.z) : pos.z;
                    if (pos.z < frontMost - 0.4f) return hinges[2];
                    return left ? hinges[0] : hinges[1];
                }
                default: return hinges[0];
            }
        }

        /// The hinge's object name: the vanilla one for the first of a kind (the game finds some by name), numbered after.
        private static string Name(string kind, string vanillaName, Vector3 pos, HashSet<string> used)
        {
            string name = vanillaName;
            for (int n = 2; !used.Add(name); n++) name = vanillaName + "_" + n;
            return name;
        }

        private static void PlaceHinge(Dictionary<string, object> node, string kind, string name, Marker m, Dictionary<string, object> a, Result res)
        {
            var oldPos = Rebuild.V3(node["pos"]);
            var oldRot = Rebuild.Q(node["rot"]);
            var newPos = m.Origin;   // a point is its object's origin (a mesh picked as one included)
            // a marker's rotation turns the vanilla hinge from how the game has it for that side (right wheels and rear
            // bumpers face the other way, steering wheels and windscreens lean): an unturned marker gives the game's
            var newRot = m.Rot * oldRot;
            if (kind == "wheel")
            {
                // the marker is the wheel's centre at rest: the hinge is the top of the suspension, half its travel above
                float travel = SpringLength(node);
                newPos += m.Rot * Vector3.up * travel * 0.5f;
            }
            bool mirror = Mathf.Sign(oldPos.x) != Mathf.Sign(newPos.x) && Mathf.Abs(oldPos.x) > 0.05f && Mathf.Abs(newPos.x) > 0.05f;
            node["pos"] = Snapshot.V(newPos);
            node["rot"] = Snapshot.V(newRot);
            node["name"] = name;
            MoveFsmPositions(node, oldPos, oldRot.eulerAngles, newPos, newRot.eulerAngles, mirror);

            // the shift a fitted part gets (its hinge edge on the hinge): the asset's if it says, the vanilla hinge's if not
            if (a.TryGetValue("offset", out var off) && off is List<object>)
            {
                var o = Spec.Vec(off, Vector3.zero);
                foreach (var act in FsmEdit.Actions(node, "vehPart_Attach"))
                    if (act.Type == "SetPosition" && !act.TargetsOwner() && act.State == "Attach")
                    {
                        act.SetFloat("x", -o.x); // the add-on writes glTF/Blender-side X; Unity's is mirrored
                        act.SetFloat("y", o.y);
                        act.SetFloat("z", o.z);
                    }
            }
            if (kind == "wheel") SetWheelFlags(node, a);
        }

        /// A wheel's suspension, if the asset sets it: travel (the spring's length), stiffness (its force at full
        /// compression), absorption (the damper's rate, bump and rebound alike).
        private static void SetSuspension(Dictionary<string, object> node, Dictionary<string, object> a)
        {
            foreach (var c in Json.Arr(node, "components") ?? new List<object>())
            {
                if (!(Json.Str(c, "$type") ?? "").StartsWith("NWH.WheelController3D.WheelController")) continue;
                var f = Json.Obj(c, "fields");
                var spring = Json.Obj(f, "spring");
                var damper = Json.Obj(f, "damper");
                if (spring != null && a.ContainsKey("travel")) spring["maxLength"] = Math.Max(0.02, Json.Num(a, "travel", 0.2));
                if (spring != null && a.ContainsKey("stiffness")) spring["maxForce"] = Math.Max(1.0, Json.Num(a, "stiffness", 5000));
                if (damper != null && a.ContainsKey("absorption"))
                {
                    double v = Math.Max(0.0, Json.Num(a, "absorption", 3000));
                    damper["bumpRate"] = v;
                    damper["reboundRate"] = v;
                }
            }
        }

        /// A wheel's two suspensions, as its Suspension FSM switches them when the lift kit goes on or off (the car's
        /// checkSuspension FSM sends Activate or Deactivate): stock, the wheel's own settings (the FSM sets them again as
        /// the car spawns, so they're written into it); lifted, the asset's lift_* settings or, where it has none, the
        /// skeleton car's change from stock to lifted on top of the wheel's own (longer travel, softer, wider and lower).
        private static void SetLiftStates(Dictionary<string, object> node, Dictionary<string, object> a, Result res)
        {
            var actions = FsmEdit.Actions(node, "Suspension").ToList();
            if (actions.Count == 0) return;
            float travel = SpringLength(node), bump = Damper(node, "bumpRate"), rebound = Damper(node, "reboundRate");
            float? vStockTravel = null, vLiftTravel = null, vStockDamp = null, vLiftDamp = null;
            foreach (var act in actions.Where(x => x.Type == "SetProperty"))
            {
                var prop = act.Var("targetProperty");
                var f = Json.Obj(prop, "FloatParameter");
                if (f == null) continue;
                float v = (float)Json.Num(f, "value");
                string what = Json.Str(prop, "PropertyName");
                bool lifted = act.State == "lifted";
                if (what == "spring.maxLength") { if (lifted) vLiftTravel = v; else vStockTravel = v; }
                else if (what == "DamperBumpRate") { if (lifted) vLiftDamp = v; else vStockDamp = v; }
            }
            float liftTravel = a.ContainsKey("lift_travel") ? Mathf.Max(0.02f, (float)Json.Num(a, "lift_travel"))
                             : travel + Mathf.Max(0f, (vLiftTravel ?? travel) - (vStockTravel ?? travel));
            float soft = vStockDamp > 0 && vLiftDamp != null ? vLiftDamp.Value / vStockDamp.Value : 1f;
            float liftBump = a.ContainsKey("lift_absorption") ? Mathf.Max(0f, (float)Json.Num(a, "lift_absorption")) : bump * soft;
            float liftRebound = a.ContainsKey("lift_absorption") ? liftBump : rebound * soft;
            foreach (var act in actions.Where(x => x.Type == "SetProperty"))
            {
                var prop = act.Var("targetProperty");
                var f = Json.Obj(prop, "FloatParameter");
                if (f == null) continue;
                bool lifted = act.State == "lifted";
                switch (Json.Str(prop, "PropertyName"))
                {
                    case "spring.maxLength": f["value"] = (double)(lifted ? liftTravel : travel); break;
                    case "DamperBumpRate": f["value"] = (double)(lifted ? liftBump : bump); break;
                    case "DamperReboundRate": f["value"] = (double)(lifted ? liftRebound : rebound); break;
                }
            }
            // where the lifted wheel sits: the stock spot moved out (away from the car's middle) and down, if the asset says
            if (!a.ContainsKey("lift_height") && !a.ContainsKey("lift_wider")) return;   // the skeleton car's, moved with the wheel
            var stock = actions.FirstOrDefault(x => x.Type == "SetPosition" && x.State == "stock" && x.TargetsOwner());
            var lift = actions.FirstOrDefault(x => x.Type == "SetPosition" && x.State == "lifted" && x.TargetsOwner());
            if (stock == null || lift == null) return;
            float? sx = stock.GetFloat("x"), sy = stock.GetFloat("y"), sz = stock.GetFloat("z");
            if (sx == null || sy == null || sz == null) return;
            float wider = (float)Json.Num(a, "lift_wider", 0), height = (float)Json.Num(a, "lift_height", 0);
            float side = Mathf.Abs(sx.Value) < 0.05f ? 0f : Mathf.Sign(sx.Value);
            lift.SetFloat("x", sx.Value + side * wider);
            lift.SetFloat("y", sy.Value - height);
            lift.SetFloat("z", sz.Value);
        }

        private static float Damper(Dictionary<string, object> node, string field)
        {
            foreach (var c in Json.Arr(node, "components") ?? new List<object>())
            {
                if (!(Json.Str(c, "$type") ?? "").StartsWith("NWH.WheelController3D.WheelController")) continue;
                return (float)Json.Num(Json.Obj(Json.Obj(c, "fields"), "damper"), field, 3000);
            }
            return 3000f;
        }

        /// The fuel tank's size (litres): the filler's LiquidAmount FSM, filled as much as the skeleton car's is.
        private static void SetFuelCapacity(Dictionary<string, object> fuel, Dictionary<string, object> spec, Result res)
        {
            if (fuel == null || !spec.ContainsKey("fuel")) return;
            double cap = Math.Max(0.1, Json.Num(spec, "fuel", 21));
            var capVar = FsmEdit.Variable(fuel, "LiquidAmount", "floatVariables", "LiquidCapacity");
            var liquid = FsmEdit.Variable(fuel, "LiquidAmount", "floatVariables", "Liquid");
            if (capVar == null) { res.Problems.Add("fuel: no LiquidAmount capacity on the skeleton's filler"); return; }
            double old = Math.Max(0.1, Json.Num(capVar, "value", 21));
            capVar["value"] = cap;
            if (liquid != null) liquid["value"] = Json.Num(liquid, "value", old) / old * cap;
        }

        private static float SpringLength(Dictionary<string, object> node)
        {
            foreach (var c in Json.Arr(node, "components") ?? new List<object>())
            {
                if (!(Json.Str(c, "$type") ?? "").StartsWith("NWH.WheelController3D.WheelController")) continue;
                return (float)Json.Num(Json.Obj(Json.Obj(c, "fields"), "spring"), "maxLength", 0.2);
            }
            return 0.2f;
        }

        /// Positions and angles a hinge's FSMs set it to (its reset spot, the suspension heights) move with the hinge.
        private static void MoveFsmPositions(Dictionary<string, object> node, Vector3 oldPos, Vector3 oldEuler, Vector3 newPos, Vector3 newEuler, bool mirror)
        {
            foreach (var act in FsmEdit.Actions(node))
            {
                if (act.FsmName == "vehPart_Attach" || !act.TargetsOwner()) continue;
                if (act.Type == "SetPosition")
                {
                    float? x = act.GetFloat("x"), y = act.GetFloat("y"), z = act.GetFloat("z");
                    if (x == null || y == null || z == null) continue;  // a nudge by a variable (the player adjusting it): relative, left alone
                    var d = new Vector3(x.Value, y.Value, z.Value) - oldPos;
                    if (mirror) d.x = -d.x;
                    var p = newPos + d;
                    act.SetFloat("x", p.x); act.SetFloat("y", p.y); act.SetFloat("z", p.z);
                }
                else if (act.Type == "SetRotation")
                {
                    float? x = act.GetFloat("xAngle"), y = act.GetFloat("yAngle"), z = act.GetFloat("zAngle");
                    if (x == null || y == null || z == null) continue;
                    var e = newEuler + (new Vector3(x.Value, y.Value, z.Value) - oldEuler);
                    act.SetFloat("xAngle", e.x); act.SetFloat("yAngle", e.y); act.SetFloat("zAngle", e.z);
                }
            }
        }

        private static void SetWheelFlags(Dictionary<string, object> node, Dictionary<string, object> a)
        {
            // steering and power are NWH's (wheel groups, differentials): set on the vehicle, see WirePowertrain
        }

        // ------------------------------------------------------------------ drivetrain

        /// Wheels, axle groups and the differentials that drive the powered wheels, written into the car's VehicleController
        /// (as NWH links them: each component names the next by a hash of its name).
        private static void WirePowertrain(Dictionary<string, object> root, Dictionary<string, object> spec, List<Dictionary<string, object>> attach,
                                           Dictionary<string, Marker> markers, Result res)
        {
            var vc = Json.Arr(root, "components").OfType<Dictionary<string, object>>().FirstOrDefault(c => (Json.Str(c, "$type") ?? "").StartsWith("NWH.VehiclePhysics2.VehicleController,"));
            var pt = Json.Obj(Json.Obj(vc, "fields"), "powertrain");
            if (pt == null) { res.Problems.Add("no NWH powertrain on the skeleton"); return; }

            // the hinge names the wheels will get (the same order as the hinges are made in Build)
            var used = new HashSet<string>();
            // steer: 1 turns with the steering wheel, -1 the other way (as rear-wheel steering), 0 not at all
            var wheels = new List<(string name, Vector3 pos, bool power, float steer)>();
            foreach (var a in attach)
            {
                string kind = Json.Str(a, "kind");
                if (!markers.TryGetValue(Json.Str(a, "node") ?? "", out var m) || kind == null || !Templates.ContainsKey(kind)) continue;
                string vname = Pick(kind, Templates[kind].hinges, m.Pos, a, attach, markers);
                string name = Name(kind, vname, m.Pos, used);
                if (kind == "wheel") wheels.Add((name, m.Pos, Json.Bool(a, "power"), Json.Bool(a, "steer") ? (Json.Bool(a, "reverse") ? -1f : 1f) : 0f));
            }
            if (wheels.Count == 0) { res.Problems.Add("no wheels"); return; }

            var tplWheel = Json.Arr(pt, "wheels")?.OfType<Dictionary<string, object>>().FirstOrDefault();
            var tplGroups = Json.Arr(pt, "wheelGroups")?.OfType<Dictionary<string, object>>().ToList() ?? new List<Dictionary<string, object>>();
            var tplDiff = Json.Arr(pt, "differentials")?.OfType<Dictionary<string, object>>().FirstOrDefault();

            // axle groups: wheels about level with each other, split by whether they steer
            var groups = new List<(float z, float steer, List<int> wheels)>();
            for (int i = 0; i < wheels.Count; i++)
            {
                var w = wheels[i];
                var g = groups.FirstOrDefault(x => x.steer == w.steer && Mathf.Abs(x.z - w.pos.z) < 0.3f);
                if (g.wheels == null) groups.Add((w.pos.z, w.steer, new List<int> { i }));
                else g.wheels.Add(i);
            }
            groups = groups.OrderByDescending(g => g.z).ToList();
            var groupJson = new List<object>();
            var wheelGroup = new int[wheels.Count];
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var tpl = g.steer != 0 ? tplGroups.FirstOrDefault(x => Json.Num(x, "steerCoefficient") > 0) : tplGroups.LastOrDefault(x => Json.Num(x, "steerCoefficient") <= 0);
                var gj = (Dictionary<string, object>)Json.Clone(tpl ?? tplGroups.FirstOrDefault() ?? new Dictionary<string, object>());
                gj["name"] = $"Axle {gi}";
                gj["steerCoefficient"] = (double)g.steer;
                gj["wheels"] = new List<object>();
                groupJson.Add(gj);
                foreach (int wi in g.wheels) wheelGroup[wi] = gi;
            }
            pt["wheelGroups"] = groupJson;

            var wheelJson = new List<object>();
            for (int i = 0; i < wheels.Count; i++)
            {
                var wj = (Dictionary<string, object>)Json.Clone(tplWheel ?? new Dictionary<string, object>());
                wj["name"] = wheels[i].name;
                wj["inputNameHash"] = 0L;
                wj["outputNameHash"] = 0L;
                wj["wheelUAPI"] = new Dictionary<string, object> { ["$ref"] = "named", ["path"] = wheels[i].name, ["type"] = "NWH.WheelController3D.WheelController", ["i"] = 0L };
                wj["wheelGroupSelector"] = new Dictionary<string, object> { ["index"] = (long)wheelGroup[i] };
                wheelJson.Add(wj);
            }
            pt["wheels"] = wheelJson;

            // the differentials: one per powered axle of two (the axle's settings), then centre ones joining axles front
            // to back (the centre's: by default locked, as the game's four-wheel drive PigPen has it; open centres let one
            // light or spinning wheel take all the drive and the car crawls)
            var diffSpec = spec.TryGetValue("diff", out var ds) ? ds as Dictionary<string, object> : null;
            var axleSet = diffSpec != null && diffSpec.TryGetValue("axle", out var ax) ? ax as Dictionary<string, object> : null;
            var centreSet = diffSpec != null && diffSpec.TryGetValue("center", out var ce) ? ce as Dictionary<string, object> : null;
            var diffs = new List<object>();
            var powered = wheels.Where(w => w.power).ToList();
            var axleOutputs = new List<string>();
            var axles = powered.GroupBy(w => Mathf.Round(w.pos.z / 0.3f)).OrderByDescending(x => x.Key).ToList();
            for (int ai = 0; ai < axles.Count; ai++)
            {
                var ws = axles[ai].OrderBy(w => w.pos.x).ToList();
                string axleName = axles.Count == 1 ? (ws[0].pos.z >= 0 ? "Front" : "Rear") : ai == 0 ? "Front" : ai == axles.Count - 1 ? "Rear" : "Middle";
                int k = 0;
                while (ws.Count > 1)
                {
                    string dn = axleName + " Differential" + (k++ > 0 ? " " + k : "");
                    while (diffs.Any(x => Json.Str(x, "name") == dn)) dn += "+";
                    var d = Diff(tplDiff, dn, ws[0].name, ws[1].name);
                    DiffSettings(d, axleSet, 0);
                    diffs.Add(d);
                    ws.RemoveRange(0, 2);
                    axleOutputs.Add(dn);
                }
                if (ws.Count == 1) axleOutputs.Add(ws[0].name);
            }
            int centres = 0;
            while (axleOutputs.Count > 1)
            {
                string dn = "Center Differential" + (centres++ > 0 ? " " + centres : "");
                var d = Diff(tplDiff, dn, axleOutputs[0], axleOutputs[1]);
                DiffSettings(d, centreSet, 1);
                diffs.Add(d);
                axleOutputs.RemoveRange(0, 2);
                axleOutputs.Add(dn);
            }
            // inputs named as NWH does (informative: it drives through outputs)
            var byName = diffs.OfType<Dictionary<string, object>>().ToDictionary(x => Json.Str(x, "name"));
            foreach (var d in byName.Values)
                foreach (var key in new[] { "outputNameHash", "outputBNameHash" })
                {
                    var child = byName.Values.FirstOrDefault(x => NameHash(Json.Str(x, "name")) == (int)Json.Num(d, key));
                    if (child != null) child["inputNameHash"] = (long)NameHash(Json.Str(d, "name"));
                }
            pt["differentials"] = diffs;
            var trans = Json.Obj(pt, "transmission");
            if (trans != null) trans["outputNameHash"] = axleOutputs.Count == 1 ? (long)NameHash(axleOutputs[0]) : 0L;
            if (powered.Count == 0) res.Problems.Add("no powered wheels: the engine drives nothing");
            if (!wheels.Any(w => w.steer != 0)) res.Problems.Add("no steering wheels");
        }

        /// A differential's settings from the asset (type: 0 open, 1 locked, 2 limited slip), else the default type.
        private static void DiffSettings(Dictionary<string, object> d, Dictionary<string, object> set, int defaultType)
        {
            d["_differentialType"] = (long)(set != null ? (int)Json.Num(set, "type", defaultType) : defaultType);
            if (set == null) return;
            if (set.ContainsKey("bias")) d["biasAB"] = Mathf.Clamp01((float)Json.Num(set, "bias", 0.5));
            if (set.ContainsKey("stiffness")) d["stiffness"] = Mathf.Clamp01((float)Json.Num(set, "stiffness", 0.5));
            if (set.ContainsKey("slip")) d["slipTorque"] = Math.Max(0.0, Json.Num(set, "slip", 400));
            if (set.ContainsKey("power")) d["powerRamp"] = Mathf.Clamp01((float)Json.Num(set, "power", 1));
            if (set.ContainsKey("coast")) d["coastRamp"] = Mathf.Clamp01((float)Json.Num(set, "coast", 0.5));
        }

        private static Dictionary<string, object> Diff(Dictionary<string, object> tpl, string name, string a, string b)
        {
            var d = (Dictionary<string, object>)Json.Clone(tpl ?? new Dictionary<string, object>());
            d["name"] = name;
            d["inputNameHash"] = 0L;
            d["outputNameHash"] = (long)NameHash(a);
            d["outputBNameHash"] = (long)NameHash(b);
            return d;
        }

        /// The hash NWH links powertrain components by (.NET's string hash, as Unity's Mono computes it).
        public static int NameHash(string s)
        {
            unchecked
            {
                int h1 = 5381, h2 = 5381;
                for (int i = 0; i < s.Length; i += 2)
                {
                    h1 = ((h1 << 5) + h1) ^ s[i];
                    if (i + 1 >= s.Length) break;
                    h2 = ((h2 << 5) + h2) ^ s[i + 1];
                }
                return h1 + h2 * 1566083941;
            }
        }

        // ------------------------------------------------------------------ colliders and functional boxes

        private static Bounds BodyBounds(Transform car, Transform body)
        {
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;
            foreach (var mf in body.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = car.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            if (!any) b = new Bounds(Vector3.zero, new Vector3(1.6f, 1.2f, 3.6f));
            return b;
        }

        private static void Solids(Transform car, List<Dictionary<string, object>> boxes, Dictionary<string, Marker> markers, Result res)
        {
            var parent = car.Find("Colliders") ?? car;
            int n = 0;
            foreach (var b in boxes.Where(x => Json.Str(x, "kind") == "solid"))
            {
                if (!markers.TryGetValue(Json.Str(b, "node") ?? "", out var m)) { res.Problems.Add($"solid: no object '{Json.Str(b, "node")}'"); continue; }
                // solid like the game's car bodies (its Car layer): the player can't look or reach through it. Only an
                // asset that asks for it (seethrough = true) gets the SeeTrough layer the game's windows have
                bool see = Json.Bool(b, "seethrough", false);
                int layer = see ? 16 : 8;
                var go = new GameObject(see ? "collider_SeeTrough" : "collider");
                go.layer = layer;
                go.transform.SetParent(parent, false);
                n++;
                if (Json.Bool(b, "mesh") && m.Mesh != null)
                {
                    // the mesh, slightly blown up, as a convex collider (a car's Rigidbody can't carry a hollow one)
                    float inflate = (float)Json.Num(b, "inflate", 0.02);
                    var src = markers[m.Node];
                    go.transform.localPosition = Vector3.zero;
                    var mesh = Inflated(m.Mesh, inflate);
                    var t = new GameObject("shape").transform; // its own transform, as the model node had it
                    t.gameObject.layer = layer;
                    t.SetParent(go.transform, false);
                    t.localPosition = m.Origin; t.localRotation = m.Rot; t.localScale = m.Scale;
                    var mc = t.gameObject.AddComponent<MeshCollider>();
                    mc.convex = true;
                    mc.sharedMesh = mesh;
                }
                else
                {
                    go.transform.localPosition = m.Pos;
                    go.transform.localRotation = m.Rot;
                    var bc = go.AddComponent<BoxCollider>();
                    bc.size = m.Size;
                }
            }
            if (n == 0) res.Problems.Add("no solid colliders: the car will fall through the ground");
        }

        /// NWH's centre of mass (it sets the Rigidbody's every physics step): the asset's point, else the skeleton
        /// car's, moved to the body; inertia the skeleton car's, scaled to this car's mass and size.
        private static void MassProperties(Transform car, List<Dictionary<string, object>> boxes, Dictionary<string, Marker> markers, Bounds bounds, float vanillaMass, Result res)
        {
            var rb = car.GetComponent<Rigidbody>();
            var com = car.GetComponent<NWH.Common.CoM.VariableCenterOfMass>();
            var at = boxes.Where(b => Json.Str(b, "kind") == "com").Select(b => markers.TryGetValue(Json.Str(b, "node") ?? "", out var m) ? m : null).FirstOrDefault(m => m != null);
            Vector3 vDims = com != null ? com.dimensions : new Vector3(1.27f, 1.15f, 3.17f);
            Vector3 point;
            if (at != null) point = at.Origin;
            else
            {
                // the skeleton car's, at the same place in this car's box
                var v = com != null ? com.centerOfMass : new Vector3(0, -0.39f, 0.1f);
                point = new Vector3(bounds.center.x + v.x * bounds.size.x / vDims.x, bounds.center.y + v.y * bounds.size.y / vDims.y, bounds.center.z + v.z * bounds.size.z / vDims.z);
            }
            if (com == null)
            {
                if (rb != null) rb.centerOfMass = point;
                return;
            }
            float m0 = com.useDefaultMass ? Mathf.Max(vanillaMass, 1f) : Mathf.Max(com.baseMass, 1f);
            float mNow = rb != null ? rb.mass : m0;
            Vector3 d = bounds.size;
            Vector3 t = com.inertiaTensor;
            // a box's inertia goes with mass and with the square of the other two sides
            float Sq(float a) => a * a;
            com.inertiaTensor = new Vector3(
                t.x * mNow / m0 * (Sq(d.y) + Sq(d.z)) / Mathf.Max(Sq(vDims.y) + Sq(vDims.z), 0.01f),
                t.y * mNow / m0 * (Sq(d.x) + Sq(d.z)) / Mathf.Max(Sq(vDims.x) + Sq(vDims.z), 0.01f),
                t.z * mNow / m0 * (Sq(d.x) + Sq(d.y)) / Mathf.Max(Sq(vDims.x) + Sq(vDims.y), 0.01f));
            com.combinedInertiaTensor = com.inertiaTensor;
            com.dimensions = d;
            com.useDefaultCenterOfMass = false;
            com.centerOfMass = point;
            com.combinedCenterOfMass = point;
            if (rb != null) rb.centerOfMass = point;
        }

        /// The switch panel's parts, by node name: the panel itself and its switches.
        public static readonly string[] PanelSwitches = { "switch_ignition", "switch_start", "switch_fuelpump", "switch_fan", "switch_lights", "horn" };

        private static string BaseName(string n)
        {
            int dot = n.LastIndexOf('.');   // Blender's "switch_fan.001"
            return dot > 0 && n.Substring(dot + 1).All(char.IsDigit) ? n.Substring(0, dot) : n;
        }

        /// A switch panel of the asset's own: its point is a mesh, or has children named as the game's switches.
        /// node name -> the vanilla object it stands for ("" for the panel, else the switch's name).
        private static Dictionary<string, string> CustomPanel(Gltf glb, List<Dictionary<string, object>> attach)
        {
            var parts = new Dictionary<string, string>();
            string panelNode = attach.Where(a => Json.Str(a, "kind") == SwitchPanel).Select(a => Json.Str(a, "node")).FirstOrDefault(x => x != null);
            var panel = panelNode == null ? null : glb.Nodes.FirstOrDefault(n => n.Name == panelNode);
            if (panel == null) return parts;
            if (panel.Mesh >= 0) parts[panel.Name] = "";
            foreach (var n in glb.Nodes)
            {
                if (n == panel) continue;
                bool under = false;
                for (var p = n.Parent; p != null; p = p.Parent) if (p == panel) { under = true; break; }
                if (!under) continue;
                string b = BaseName(n.Name);
                if (PanelSwitches.Contains(b) && !parts.ContainsValue(b)) parts[n.Name] = b;
            }
            return parts;
        }

        private static void FitCustomPanel(Transform car, Dictionary<string, string> parts, Gltf glb, Dictionary<string, Marker> markers,
                                           List<Dictionary<string, object>> boxes, Result res)
        {
            if (parts.Count == 0) return;
            var panel = car.Find("switch_panel");
            if (panel == null) { res.Problems.Add("switch panel: none on the skeleton"); return; }
            foreach (var kv in parts)
            {
                var n = glb.Nodes.FirstOrDefault(x => x.Name == kv.Key);
                if (n == null || !markers.TryGetValue(kv.Key, out var mk)) continue;
                var target = kv.Value == "" ? panel : panel.Find(kv.Value);
                if (target == null) { res.Problems.Add($"switch panel: no {kv.Value} on the skeleton's panel"); continue; }
                if (kv.Value != "")
                {
                    // the switch where the asset's is, turning about its origin
                    target.position = car.TransformPoint(mk.Origin);
                    target.rotation = car.rotation * mk.Rot;
                }
                if (n.Mesh < 0 || n.Object == null) continue;   // an empty: the vanilla switch, put there
                var mf = n.Object.GetComponent<MeshFilter>();
                var mr = n.Object.GetComponent<MeshRenderer>();
                if (mf == null || mr == null) continue;
                // the asset's mesh, drawn in the object's place (the panel's scale is the object's)
                var tf = target.GetComponent<MeshFilter>() ?? target.gameObject.AddComponent<MeshFilter>();
                var tr = target.GetComponent<MeshRenderer>() ?? target.gameObject.AddComponent<MeshRenderer>();
                tf.sharedMesh = mf.sharedMesh;
                tr.sharedMaterials = mr.sharedMaterials;
                tr.enabled = true;
                var parentScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
                target.localScale = new Vector3(mk.Scale.x / parentScale.x, mk.Scale.y / parentScale.y, mk.Scale.z / parentScale.z);
            }
            // switches of the panel that the asset hasn't got stay where the vanilla panel has them, moved with it

            // the light switch's clickable box, where the asset's headlights box is, now that the switch has moved
            var sw = panel.Find("switch_lights");
            var lb = boxes.Where(b => Json.Str(b, "kind") == "headlights").Select(b => markers.TryGetValue(Json.Str(b, "node") ?? "", out var m) ? m : null).FirstOrDefault(m => m != null);
            if (lb != null && sw != null) FitSwitchSphere(car, sw, lb);
        }

        /// The light switch's trigger sphere, over a box of the asset's (centre in the switch's space, the box's half size).
        private static void FitSwitchSphere(Transform car, Transform sw, Marker box)
        {
            var sc = sw.GetComponent<SphereCollider>();
            if (sc == null) { sc = sw.gameObject.AddComponent<SphereCollider>(); sc.isTrigger = true; }
            sc.center = sw.InverseTransformPoint(car.TransformPoint(box.Pos));
            float scale = Mathf.Max(Mathf.Abs(sw.lossyScale.x), Mathf.Abs(sw.lossyScale.y), Mathf.Abs(sw.lossyScale.z), 1e-4f);
            sc.radius = Mathf.Max(box.Size.x, box.Size.y, box.Size.z) * 0.5f / scale;
            foreach (var bc in sw.GetComponents<BoxCollider>()) Object.DestroyImmediate(bc);
        }

        internal static Mesh Inflated(Mesh src, float inflate)
        {
            var mesh = Object.Instantiate(src);
            mesh.name = src.name + " (collider)";
            var c = src.bounds.center;
            var ext = src.bounds.extents;
            var v = mesh.vertices;
            for (int i = 0; i < v.Length; i++)
            {
                var d = v[i] - c;
                v[i] = c + new Vector3(d.x * (1 + inflate / Mathf.Max(ext.x, 0.01f)), d.y * (1 + inflate / Mathf.Max(ext.y, 0.01f)), d.z * (1 + inflate / Mathf.Max(ext.z, 0.01f)));
            }
            mesh.vertices = v;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void PlaceSkeleton(Transform car, Dictionary<string, Dictionary<string, object>> vanilla, List<Dictionary<string, object>> attach,
                                          List<Dictionary<string, object>> boxes, Dictionary<string, Marker> markers,
                                          Dictionary<Dictionary<string, object>, Transform> hinges, Bounds bounds, Result res)
        {
            Vector3 VPos(string namePath)
            {
                // a vanilla object's position relative to the vanilla car's root
                var node = vanilla.Values.FirstOrDefault(n => Json.Str(n, "path") != null && NamePathOf(vanilla, Json.Str(n, "path")) == namePath);
                if (node == null) return Vector3.zero;
                var p = Vector3.zero; var r = Quaternion.identity;
                foreach (var step in Chain(vanilla, Json.Str(node, "path")))
                {
                    p += r * Rebuild.V3(step["pos"]);
                    r = r * Rebuild.Q(step["rot"]);
                }
                return p;
            }
            Quaternion VRot(string namePath)
            {
                var node = vanilla.Values.FirstOrDefault(n => Json.Str(n, "path") != null && NamePathOf(vanilla, Json.Str(n, "path")) == namePath);
                var r = Quaternion.identity;
                if (node != null) foreach (var step in Chain(vanilla, Json.Str(node, "path"))) r = r * Rebuild.Q(step["rot"]);
                return r;
            }
            Marker Box(string kind) => boxes.Where(b => Json.Str(b, "kind") == kind).Select(b => markers.TryGetValue(Json.Str(b, "node") ?? "", out var m) ? m : null).FirstOrDefault(m => m != null);
            Marker Point(string name) => name != null && markers.TryGetValue(name, out var m) ? m : null;
            Transform Hinge(string kind) => attach.Where(a => Json.Str(a, "kind") == kind && hinges.ContainsKey(a)).Select(a => hinges[a]).FirstOrDefault();
            void SetLocal(Transform t, Vector3 carPos) { if (t != null) t.position = car.TransformPoint(carPos); }

            // driving: where the player gets in (the drive trigger's sphere, on the seat), the driver's view (the eyes:
            // sitting, the player's camera is EyeHeight above sitPos, which the player is put on) and where they get out
            var driverBox = boxes.FirstOrDefault(b => Json.Str(b, "kind") == "driver");
            var drive = car.Find("DriveTrigger");
            if (driverBox != null && drive != null)
            {
                bool newStyle = driverBox.ContainsKey("radius");   // older exports: a box zone and a sit point (sitPos itself)
                var entry = markers.TryGetValue(Json.Str(driverBox, "node") ?? "", out var en) ? en : null;
                var view = Point(Json.Str(driverBox, "view"));
                var sitOld = Point(Json.Str(driverBox, "sit"));
                var exit = Point(Json.Str(driverBox, "exit"));
                Vector3 vDrive = VPos("DriveTrigger"), vSit = VPos("DriveTrigger/sitPos"), vModel = VPos("parts/PlayerModel_Sit");
                Quaternion sitRot = view != null ? view.Rot : Quaternion.identity;
                Vector3? sitAt = view != null ? view.Origin - sitRot * Vector3.up * EyeHeight
                               : sitOld != null ? sitOld.Pos
                               : newStyle && entry != null ? entry.Origin + (vSit - vDrive)
                               : entry != null ? (Vector3?)entry.Pos : null;
                if (sitAt == null) res.Problems.Add("driver: no driver's view point and no seat collider: nobody can get in");
                else
                {
                    Vector3 sitPos = sitAt.Value;
                    Vector3 drivePos = newStyle && entry != null ? entry.Origin : sitPos - (vSit - vDrive);
                    drive.position = car.TransformPoint(drivePos);
                    drive.rotation = car.rotation;
                    var sp = drive.Find("sitPos");
                    if (sp != null) { sp.position = car.TransformPoint(sitPos); sp.rotation = car.rotation * sitRot; }
                    if (exit != null) SetLocal(drive.Find("exitPos"), exit.Origin);
                    else SetLocal(drive.Find("exitPos"), new Vector3(sitPos.x < 0 ? bounds.min.x - 0.6f : bounds.max.x + 0.6f, sitPos.y, sitPos.z));
                    if (newStyle)
                    {
                        // the sphere the player looks at to get in: the asset's size (the switched-off box stays as the game has it)
                        float radius = Mathf.Max(0.05f, (float)Json.Num(driverBox, "radius", 0.2));
                        foreach (var sc in drive.GetComponents<SphereCollider>()) { sc.center = Vector3.zero; sc.radius = radius; }
                    }
                    else
                        foreach (var bc in drive.GetComponents<BoxCollider>())
                        {
                            bc.center = drive.InverseTransformPoint(car.TransformPoint(entry != null ? entry.Pos : sitPos));
                            if (entry != null) bc.size = entry.Size;
                        }
                    SetLocal(car.Find("parts/PlayerModel_Sit"), sitPos + (vModel - vSit));
                    SetLocal(car.Find("parts/pedals"), sitPos + (VPos("parts/pedals") - vSit));
                    SetLocal(car.Find("handbrake"), sitPos + (VPos("handbrake") - vSit));
                    if (view == null && sitOld == null) res.Problems.Add("driver: no driver's view point (put where the game's car has it from the seat)");
                }
            }
            else res.Problems.Add("no driver's seat: nobody can get in");

            // the handbrake, where the asset puts it. Its FSM turns it in its own parent's space (40° on, 70° off), so it
            // hangs from a mount turned as the marker is: it pulls the way the asset has it
            var hbAt = attach.Where(x => Json.Str(x, "kind") == "handbrake").Select(x => Point(Json.Str(x, "node"))).FirstOrDefault(x => x != null);
            var hb = car.Find("handbrake");
            if (hb != null && hbAt != null)
            {
                var mount = new GameObject("handbrake_mount").transform;
                mount.gameObject.layer = hb.gameObject.layer;
                mount.SetParent(car, false);
                mount.position = car.TransformPoint(hbAt.Origin);
                mount.rotation = car.rotation * hbAt.Rot;
                hb.SetParent(mount, false);
                hb.localPosition = Vector3.zero;
                hb.localRotation = VRot("handbrake");
            }

            // ignition
            var ign = Box("ignition");
            var start = car.Find("START");
            if (ign != null && start != null)
            {
                start.position = car.TransformPoint(ign.Pos);
                start.rotation = car.rotation * ign.Rot;
                foreach (var bc in start.GetComponents<BoxCollider>()) { bc.center = Vector3.zero; bc.size = ign.Size; }
            }
            else if (ign == null) res.Problems.Add("no ignition box: the engine can't be started");

            // steering columns: the vanilla one, where the asset puts it (turned as its marker is from the game's)
            var rod = car.Find("parts/steering rod");
            bool firstRod = true;
            foreach (var a in attach.Where(x => Json.Str(x, "kind") == "steeringwheel"))
            {
                var col = Point(Json.Str(a, "column"));
                if (col == null || rod == null) continue;
                var t = firstRod ? rod : Object.Instantiate(rod.gameObject, rod.parent).transform;
                if (!firstRod) t.name = rod.name;
                firstRod = false;
                t.position = car.TransformPoint(col.Origin);
                t.rotation = car.rotation * col.Rot * VRot("parts/steering rod");
            }

            // the switch panel (ignition, starter, fuel pump, fan, lights, horn): the asset's, else by the steering wheel, unseen
            var steer = Hinge("steeringwheel");
            var panel = car.Find("switch_panel");
            var panelAt = attach.Where(x => Json.Str(x, "kind") == SwitchPanel).Select(x => Point(Json.Str(x, "node"))).FirstOrDefault(x => x != null);
            if (panel != null && panelAt != null)
            {
                panel.position = car.TransformPoint(panelAt.Origin);
                panel.rotation = car.rotation * panelAt.Rot * VRot("switch_panel");
            }
            else if (panel != null && steer != null) panel.position = steer.position + car.rotation * (VPos("switch_panel") - VPos("hinge_steeringwheel"));
            var lights = Box("headlights");
            var sw = car.Find("switch_panel/switch_lights");
            if (lights != null && sw != null && panelAt != null)
            {
                // the switch stays on the panel; its own trigger sphere (as the game has it) covers the asset's box
                FitSwitchSphere(car, sw, lights);
            }
            else if (lights != null && sw != null)
            {
                // no panel of the asset's: the (unseen) light switch goes to the box
                sw.position = car.TransformPoint(lights.Pos);
                sw.rotation = car.rotation * lights.Rot;
                FitSwitchSphere(car, sw, lights);
            }

            // the front panel: the dashboard's gauge slot (gauges held in its box are fitted where they're held): the
            // asset's box, else by the steering wheel as on the vanilla car
            var dash = car.Find("Dashboard");
            var front = Box("frontpanel");
            if (dash != null && front != null)
            {
                dash.position = car.TransformPoint(front.Pos);
                dash.rotation = car.rotation * front.Rot;
                foreach (var bc in dash.GetComponents<BoxCollider>()) { bc.center = Vector3.zero; bc.size = front.Size; bc.isTrigger = true; }
            }
            else if (dash != null && steer != null) dash.position = steer.position + car.rotation * (VPos("Dashboard") - VPos("hinge_steeringwheel"));

            // the physics lock: boxes that carry loose things with the car
            var lockT = car.Find("parts/PhysicsLock");
            if (lockT != null)
            {
                foreach (var bc in lockT.GetComponents<BoxCollider>()) Object.DestroyImmediate(bc);
                lockT.localPosition = Vector3.zero; lockT.localRotation = Quaternion.identity;
                lockT.position = car.position; lockT.rotation = car.rotation;
                int i = 0;
                foreach (var b in boxes.Where(x => Json.Str(x, "kind") == "physlock"))
                {
                    if (!markers.TryGetValue(Json.Str(b, "node") ?? "", out var m)) continue;
                    var go = new GameObject("lock_" + i++) { layer = lockT.gameObject.layer };
                    go.transform.SetParent(lockT, false);
                    go.transform.position = car.TransformPoint(m.Pos);
                    go.transform.rotation = car.rotation * m.Rot;
                    var bc = go.AddComponent<BoxCollider>();
                    bc.isTrigger = true;
                    bc.size = m.Size;
                }
            }

            // engine sound where the engine is
            var engine = Hinge("engine");
            var rsound = car.Find("RSound");
            if (rsound != null) rsound.position = engine != null ? engine.position : car.TransformPoint(new Vector3(0, bounds.center.y, bounds.max.z - 0.6f));

            // fuel filler: the asset's cap, else its filler box (older exports), else at the left rear
            var capAt = attach.Where(x => Json.Str(x, "kind") == "fuelcap").Select(x => Point(Json.Str(x, "node"))).FirstOrDefault(x => x != null);
            var fuelBox = Box("fuel");
            var fuel = car.Find("Fuel");
            if (fuel != null && capAt != null)
            {
                fuel.position = car.TransformPoint(capAt.Origin);
                fuel.rotation = car.rotation * capAt.Rot * VRot("Fuel");
                // its size: the point's scale on the cap (and the spheres you look at to use it, so it's as easy to
                // hit); the fill trigger and the hose stay as the game has them
                var size = Abs(capAt.Scale);
                if ((size - Vector3.one).sqrMagnitude > 1e-4f && size.x > 1e-3f && size.y > 1e-3f && size.z > 1e-3f)
                {
                    var cap = fuel.Find("mesh");
                    if (cap != null) cap.localScale = Vector3.Scale(cap.localScale, size);
                    float grow = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                    foreach (var sc in fuel.GetComponents<SphereCollider>()) sc.radius *= grow;
                    var syphon = fuel.Find("Syphon");
                    if (syphon != null) foreach (var sc in syphon.GetComponents<SphereCollider>()) sc.radius *= grow;
                }
            }
            else if (fuel != null)
                fuel.position = car.TransformPoint(fuelBox != null ? fuelBox.Pos : new Vector3(bounds.min.x + 0.05f, bounds.center.y, bounds.min.z + bounds.size.z * 0.3f));

            // the toolsets, where the asset puts them
            foreach (var kv in new[] { ("toolbox", "parts/toolset_1"), ("adjuster", "parts/toolset_2") })
            {
                var at = attach.Where(x => Json.Str(x, "kind") == kv.Item1).Select(x => Point(Json.Str(x, "node"))).FirstOrDefault(x => x != null);
                var t = car.Find(kv.Item2);
                if (at == null || t == null) continue;
                t.position = car.TransformPoint(at.Origin);
                t.rotation = car.rotation * at.Rot * VRot(kv.Item2);
            }

            // fuel tanks, to look at: the vanilla one where each marker is
            var tankT = car.Find("parts/fueltank");
            bool firstTank = true;
            foreach (var a in attach.Where(x => Json.Str(x, "kind") == "fueltank"))
            {
                var at = Point(Json.Str(a, "node"));
                if (at == null || tankT == null) continue;
                var t = firstTank ? tankT : Object.Instantiate(tankT.gameObject, tankT.parent).transform;
                if (!firstTank) t.name = tankT.name;
                firstTank = false;
                t.position = car.TransformPoint(at.Origin);
                t.rotation = car.rotation * at.Rot * VRot("parts/fueltank");
            }

            // damage zone at the front, hitch at the back
            var frontBumper = attach.Where(a => Json.Str(a, "kind") == "bumper" && hinges.ContainsKey(a)).Select(a => hinges[a]).OrderByDescending(t => car.InverseTransformPoint(t.position).z).FirstOrDefault();
            var rearBumper = attach.Where(a => Json.Str(a, "kind") == "bumper" && hinges.ContainsKey(a)).Select(a => hinges[a]).OrderBy(t => car.InverseTransformPoint(t.position).z).FirstOrDefault();
            var attack = car.Find("CarAttack");
            var ram = Box("attack");
            if (attack != null && ram != null)
            {
                // the asset's ram box: enemies it hits faster than 5 take speed x 3 (or the fitted front bumper's) damage
                attack.position = car.TransformPoint(ram.Pos);
                attack.rotation = car.rotation * ram.Rot;
                foreach (var bc in attack.GetComponents<BoxCollider>()) { bc.center = Vector3.zero; bc.size = ram.Size; bc.isTrigger = true; }
            }
            else if (attack != null)
            {
                attack.position = car.TransformPoint(new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.3f, bounds.max.z - 0.1f));
                foreach (var bc in attack.GetComponents<BoxCollider>()) bc.size = new Vector3(bounds.size.x * 0.85f, bc.size.y, bc.size.z);
            }
            var hitch = car.Find("hitch");
            var hitchRay = car.Find("hitch_raycast");
            Vector3 hitchPos = rearBumper != null && car.InverseTransformPoint(rearBumper.position).z < 0
                ? car.InverseTransformPoint(rearBumper.position) + new Vector3(0, -0.1f, -0.05f)
                : new Vector3(0, bounds.min.y + 0.2f, bounds.min.z - 0.05f);
            SetLocal(hitch, hitchPos);
            SetLocal(hitchRay, hitchPos + (VPos("hitch_raycast") - VPos("hitch")));

            // the lift kit slot: where the asset puts it, or between the front wheels
            var frontWheels = attach.Where(a => Json.Str(a, "kind") == "wheel" && hinges.ContainsKey(a)).Select(a => car.InverseTransformPoint(hinges[a].position)).ToList();
            var susp = car.Find("hinge_suspension_parent");
            var kitAt = attach.Where(x => Json.Str(x, "kind") == "suspension").Select(x => Point(Json.Str(x, "node"))).FirstOrDefault(x => x != null);
            var kitSlot = susp != null ? susp.Find("hinge_suspension") : null;
            if (kitSlot != null && kitSlot.GetComponent<LiftKitRelay>() == null) kitSlot.gameObject.AddComponent<LiftKitRelay>();
            if (susp != null && kitAt != null)
            {
                susp.position = car.TransformPoint(kitAt.Origin);
                susp.rotation = car.rotation * kitAt.Rot * VRot("hinge_suspension_parent");
            }
            else if (susp != null && frontWheels.Count > 0)
            {
                float maxZ = frontWheels.Max(p => p.z);
                var fw = frontWheels.Where(p => p.z > maxZ - 0.3f).ToList();
                SetLocal(susp, new Vector3(fw.Average(p => p.x), fw.Average(p => p.y) - 0.1f, fw.Average(p => p.z)));
            }
        }

        private static IEnumerable<Dictionary<string, object>> Chain(Dictionary<string, Dictionary<string, object>> index, string path)
        {
            var list = new List<Dictionary<string, object>>();
            string p = path;
            while (!string.IsNullOrEmpty(p))
            {
                if (index.TryGetValue(p, out var n)) list.Insert(0, n);
                int cut = p.LastIndexOf('/');
                p = cut < 0 ? "" : p.Substring(0, cut);
            }
            return list;
        }

        private static string NamePathOf(Dictionary<string, Dictionary<string, object>> index, string path) =>
            string.Join("/", Chain(index, path).Select(n => Json.Str(n, "name")));
    }
}
