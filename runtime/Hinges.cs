using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocars
{
    /// Doors, hoods and trunk lids of any size on any car.
    ///
    /// How the game does it: a car part's origin is the middle of its mesh. A car's door, hood or trunk hinge moves
    /// whatever is fitted to it by a fixed amount (the SetPosition in the hinge's vehPart_Attach FSM; on the Poloska's
    /// door hinge, about half a metre back), so a part of the size that car was made for has its hinge edge on the hinge.
    /// The part opens by turning to a fixed angle (the iTweenRotateTo in its own DoorOpen FSM: about Y for a door, about
    /// X for a hood), which swings it the right way only on the side and end of the car it was made for.
    ///
    /// What Apocars does: the part is measured (the box of the meshes it shows) and placed so its hinge edge sits on the
    /// hinge, and its opening angle is mirrored when it would swing the wrong way (a door into the cabin, a lid down).
    /// HingeSlot does this on the hinges of an Apocars car, for any part. HingedPart does it on an Apocars part fitted
    /// to a game car's hinge. The part's own angle comes back when it's taken off.
    internal static class Hinges
    {
        public enum Edge { Front, Rear }
        public enum Swing { AsIs, Out, Up }

        /// How a kind of part hangs on its hinge.
        public sealed class Rule
        {
            public Edge Edge = Edge.Front;   // which end of the part goes on the hinge
            public bool Flush;               // its top level with the hinge (plus Top)
            public float Top, Side, Along;   // metres: up, sideways (+X), along the car (+ forward)
            public Swing Swing = Swing.Out;  // which way it has to open
        }

        public const float CheckEvery = 0.2f;    // seconds between checks of what's fitted
        public const float AlreadyRight = 0.05f; // a hinge edge this close to the part's origin needs no moving

        public static Rule For(string kind)
        {
            if (kind == "door") return new Rule { Edge = Edge.Front, Swing = Swing.Out };
            if (kind == "hood") return new Rule { Edge = Edge.Rear, Flush = true, Along = -0.02f, Swing = Swing.Up };
            if (kind == "trunk") return new Rule { Edge = Edge.Front, Flush = true, Swing = Swing.Up };
            return null;
        }

        /// From a spec's fit value: true (the kind's rule), false (none), or a table that changes the kind's rule
        /// { edge = "front"|"rear", flush = bool, top, x, z, open = "out"|"up"|false }.
        public static Rule FromSpec(object fit, string kind)
        {
            if (fit is bool on) return on ? For(kind) : null;
            if (!(fit is Dictionary<string, object> t)) return For(kind);
            var r = For(kind) ?? new Rule();
            if (t.ContainsKey("edge")) r.Edge = Json.Str(t, "edge") == "rear" ? Edge.Rear : Edge.Front;
            if (t.ContainsKey("flush")) r.Flush = Json.Bool(t, "flush");
            r.Top = (float)Json.Num(t, "top", r.Top);
            r.Side = (float)Json.Num(t, "x", r.Side);
            r.Along = (float)Json.Num(t, "z", r.Along);
            if (t.TryGetValue("open", out var o))
                r.Swing = o is string s ? (s == "up" ? Swing.Up : s == "out" ? Swing.Out : Swing.AsIs) : Swing.AsIs;
            return r;
        }

        /// The box around the meshes a part shows, in the part's own space; null when it shows none.
        public static Bounds? Measure(Transform part)
        {
            var toPart = part.worldToLocalMatrix;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            foreach (var r in part.GetComponentsInChildren<MeshRenderer>(false))
            {
                var mesh = r.enabled ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (mesh == null) continue;
                var m = toPart * r.transform.localToWorldMatrix;
                var b = mesh.bounds;
                foreach (float x in new[] { b.min.x, b.max.x })
                foreach (float y in new[] { b.min.y, b.max.y })
                foreach (float z in new[] { b.min.z, b.max.z })
                {
                    var p = m.MultiplyPoint3x4(new Vector3(x, y, z));
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            if (float.IsInfinity(min.x)) return null;
            var box = new Bounds();
            box.SetMinMax(min, max);
            return box;
        }

        /// How far a game hinge moves what's fitted to it (its vehPart_Attach FSM), or null if it doesn't.
        public static Vector3? Shift(Transform hinge)
        {
            var fsm = hinge.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "vehPart_Attach");
            if (fsm?.Fsm == null) return null;
            foreach (var state in fsm.FsmStates)
            {
                FsmStateAction[] actions;
                try { actions = state.Actions; } catch { continue; }
                var set = actions.FirstOrDefault(a => a != null && a.GetType().Name == "SetPosition");
                if (set == null) continue;
                return new Vector3(Float(set, "x"), Float(set, "y"), Float(set, "z"));
            }
            return null;
        }

        private static float Float(object action, string field) =>
            action.GetType().GetField(field)?.GetValue(action) is FsmFloat f && !f.IsNone ? f.Value : 0f;

        /// The opening angle of a part: the vector a DoorOpen iTweenRotateTo turns it to. Null when it has none, and
        /// "wait" when its FSM hasn't started yet (the game starts it once the part is fitted).
        public static FsmVector3 OpenAngle(GameObject part, out bool wait)
        {
            wait = false;
            var fsm = part.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "DoorOpen");
            if (fsm == null) return null;
            if (fsm.Fsm == null || !fsm.Fsm.Initialized) { wait = true; return null; }
            return fsm.FsmStates
                .SelectMany(s => s.Actions ?? new FsmStateAction[0])
                .Where(a => a != null && a.GetType().Name == "iTweenRotateTo")
                .Select(a => a.GetType().GetField("vectorRotation")?.GetValue(a) as FsmVector3)
                .FirstOrDefault(v => v != null && v.Value.sqrMagnitude > 1f);
        }

        /// Whichever of the angle and its mirror swings the part's middle (where it hangs) the wanted way.
        /// side: the hinge's distance from the car's centre line (+ on the car's right).
        public static Vector3 Better(Vector3 angle, Swing swing, Vector3 middle, float side)
        {
            float Gain(Vector3 euler)
            {
                var moved = Quaternion.Euler(euler) * middle;
                return swing == Swing.Up ? moved.y : Mathf.Abs(side + moved.x);
            }
            return Gain(-angle) > Gain(angle) + 0.01f ? -angle : angle;
        }

        /// One fitted part: where it goes, and its opening angle as found and as set.
        public sealed class Fitted
        {
            public Vector3 Place;      // the part's position on its hinge
            public Vector3 Middle;     // the middle of its box, placed
            public Swing Swing;
            public bool Decided;       // its angle is settled (or there's nothing to settle)
            public int Waits;
            public FsmVector3 Angle;   // the angle it opens to, when it was mirrored
            public Vector3 Own;        // its angle before that

            public void Apply(Transform part)
            {
                if ((part.localPosition - Place).sqrMagnitude > 1e-6f) part.localPosition = Place;
            }

            /// Looks for the part's opening angle and mirrors it if need be; side: as for Better.
            public void Decide(GameObject part, float side, string where)
            {
                if (Decided) return;
                var angle = OpenAngle(part, out bool wait);
                if (angle == null)
                {
                    if (wait && ++Waits < 50) return;   // give its FSM ten seconds to start
                    Decided = true;
                    if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"Hinges: {part.name} on {where} has no opening angle; left as it is.");
                    return;
                }
                Decided = true;
                if (Swing == Swing.Out && Mathf.Abs(side) < 0.05f) return;   // a hinge on the centre line has no outside
                var want = Better(angle.Value, Swing, Middle, side);
                if (want == angle.Value) return;
                Own = angle.Value;
                Angle = angle;
                angle.Value = want;
                if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"Hinges: {part.name} on {where} opens {Swing}: angle {Own} mirrored to {want}.");
            }

            /// The part's own angle back (it's off its hinge).
            public void Undo()
            {
                if (Angle != null) Angle.Value = Own;
                Angle = null;
            }
        }

        public static float SideOf(Transform hinge) => hinge.root.InverseTransformPoint(hinge.position).x;

        public static string Where(Transform hinge) => hinge.root.name + "/" + hinge.name;
    }

    /// On a door, hood or trunk hinge of an Apocars car: hangs any part fitted there by its hinge edge, opening the
    /// right way.
    public class HingeSlot : MonoBehaviour
    {
        public string edge = "front";
        public bool flush;
        public float top, side, along;
        public string swing = "out";

        private readonly Dictionary<Transform, Hinges.Fitted> _fitted = new Dictionary<Transform, Hinges.Fitted>();
        private float _next;

        internal void Use(Hinges.Rule r)
        {
            edge = r.Edge == Hinges.Edge.Rear ? "rear" : "front";
            flush = r.Flush;
            top = r.Top;
            side = r.Side;
            along = r.Along;
            swing = r.Swing.ToString().ToLowerInvariant();
        }

        private Hinges.Swing SwingWanted => swing == "up" ? Hinges.Swing.Up : swing == "out" ? Hinges.Swing.Out : Hinges.Swing.AsIs;

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + Hinges.CheckEvery;
            var here = new HashSet<Transform>();
            foreach (Transform part in transform)
            {
                if (!part.CompareTag("vehPart")) continue;
                here.Add(part);
                if (!_fitted.TryGetValue(part, out var f)) _fitted[part] = f = Fit(part);
                if (f == null) continue;
                f.Apply(part);
                f.Decide(part.gameObject, Hinges.SideOf(transform), Hinges.Where(transform));
            }
            foreach (var gone in _fitted.Keys.Where(p => !here.Contains(p)).ToList())
            {
                _fitted[gone]?.Undo();
                _fitted.Remove(gone);
            }
        }

        /// Where a part goes on this hinge; null when it's made for it already (or shows nothing).
        private Hinges.Fitted Fit(Transform part)
        {
            var box = Hinges.Measure(part);
            if (box == null) return null;
            var b = box.Value;
            float hingeEdge = edge == "rear" ? b.min.z : b.max.z;
            if (Mathf.Abs(hingeEdge) < Hinges.AlreadyRight) return null;
            var place = new Vector3(side, flush ? top - b.max.y : 0f, along - hingeEdge);
            if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"Hinges: {part.name} on {Hinges.Where(transform)} placed at {place}.");
            return new Hinges.Fitted { Place = place, Middle = b.center + place, Swing = SwingWanted, Decided = SwingWanted == Hinges.Swing.AsIs };
        }

        private void OnDisable()
        {
            foreach (var f in _fitted.Values) f?.Undo();
            _fitted.Clear();
        }
    }

    /// On an Apocars door, hood or trunk lid: fitted to a game car's hinge, it hangs by the end that hinge expects
    /// (the hinge that moves parts back gets their front edge, one that moves them forward their rear edge) and
    /// opens the right way. On an Apocars car's hinge, HingeSlot does that instead.
    public class HingedPart : MonoBehaviour
    {
        public string swing = "out";

        private Transform _hinge;
        private Hinges.Fitted _fitted;
        private Bounds? _box;
        private bool _measured, _skip;
        private float _next;

        private Hinges.Swing SwingWanted => swing == "up" ? Hinges.Swing.Up : swing == "out" ? Hinges.Swing.Out : Hinges.Swing.AsIs;

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + Hinges.CheckEvery;
            var parent = transform.parent;
            var hinge = parent != null && CompareTag("vehPart") && parent.GetComponent<HingeSlot>() == null ? parent : null;
            if (hinge != _hinge)
            {
                _fitted?.Undo();
                _fitted = null;
                _skip = false;
                _hinge = hinge;
            }
            if (hinge == null || _skip) return;
            if (_fitted == null)
            {
                if (!_measured) { _box = Hinges.Measure(transform); _measured = true; }
                if (_box == null) { _skip = true; return; }
                var b = _box.Value;
                var shift = Hinges.Shift(hinge);
                float z = shift == null || Mathf.Abs(shift.Value.z) < 0.001f ? 0f
                        : shift.Value.z < 0 ? -b.max.z     // parts go behind this hinge: their front edge on it
                        : -b.min.z;                         // parts go in front of it: their rear edge on it
                var place = new Vector3(0, 0, z);
                _fitted = new Hinges.Fitted { Place = place, Middle = b.center + place, Swing = SwingWanted, Decided = SwingWanted == Hinges.Swing.AsIs };
                if (Plugin.VerboseLog.Value) Plugin.Log.LogInfo($"Hinges: {name} on {Hinges.Where(hinge)} placed at {place} (the hinge moves parts by {shift?.ToString() ?? "nothing"}).");
            }
            _fitted.Apply(transform);
            _fitted.Decide(gameObject, Hinges.SideOf(hinge), Hinges.Where(hinge));
        }
    }
}
