using System;
using System.Collections.Generic;
using System.Linq;

namespace Apocars
{
    /// Reads and changes PlayMaker actions inside a snapshot (before it is built): each action of each state of each FSM
    /// on a node, its parameters by name, as PlayMaker saves them (ActionData: typed parameter lists, plain values in a
    /// byte array, event names in the string list).
    internal static class FsmEdit
    {
        public sealed class Action
        {
            public Dictionary<string, object> Fsm;      // the Fsm object of the component
            public string FsmName, State;
            public string Type;                         // short type name (SetPosition)
            public int Index;                           // within the state
            internal Dictionary<string, object> Data;   // the state's ActionData
            internal int First, End;                    // its parameters: [First, End)

            public IEnumerable<string> ParamNames => Enumerable.Range(First, End - First).Select(i => (string)L(Data, "paramName")[i]);

            private int Param(string name)
            {
                var names = L(Data, "paramName");
                for (int i = First; i < End; i++) if ((string)names[i] == name) return i;
                return -1;
            }

            private string Kind(int p) => Types[(int)Json.ToDouble(L(Data, "paramDataType")[p])];
            private int Pos(int p) => (int)Json.ToDouble(L(Data, "paramDataPos")[p]);

            /// An FsmX parameter's saved object ({ useVariable, name, value }), or null.
            public Dictionary<string, object> Var(string name)
            {
                int p = Param(name);
                if (p < 0 || !Lists.TryGetValue(Kind(p), out var list)) return null;
                var l = L(Data, list);
                int pos = Pos(p);
                return pos >= 0 && pos < l.Count ? l[pos] as Dictionary<string, object> : null;
            }

            /// A float parameter, plain (Float) or FsmFloat with a constant value; null if it is a variable.
            public float? GetFloat(string name)
            {
                int p = Param(name);
                if (p < 0) return null;
                if (Kind(p) == "Float") return BitConverter.ToSingle(Bytes(), Pos(p));
                var v = Var(name);
                if (v == null || Json.Bool(v, "useVariable")) return null;
                return (float)Json.Num(v, "value");
            }

            public bool SetFloat(string name, float value)
            {
                int p = Param(name);
                if (p < 0) return false;
                if (Kind(p) == "Float")
                {
                    var b = Bytes();
                    Array.Copy(BitConverter.GetBytes(value), 0, b, Pos(p), 4);
                    Data["byteData"] = new Dictionary<string, object> { ["$b64"] = Convert.ToBase64String(b) };
                    return true;
                }
                var v = Var(name);
                if (v == null || Json.Bool(v, "useVariable")) return false;
                v["value"] = (double)value;
                return true;
            }

            public string GetString(string name)
            {
                int p = Param(name);
                if (p < 0) return null;
                string k = Kind(p);
                if (k == "String" || k == "FsmEvent")
                {
                    var sp = L(Data, "stringParams");
                    int pos = Pos(p);
                    return pos >= 0 && pos < sp.Count ? sp[pos] as string : null;
                }
                var v = Var(name);
                return v != null && !Json.Bool(v, "useVariable") ? Json.Str(v, "value") : null;
            }

            public bool SetString(string name, string value)
            {
                int p = Param(name);
                if (p < 0) return false;
                string k = Kind(p);
                if (k == "String")
                {
                    var sp = L(Data, "stringParams");
                    sp[Pos(p)] = value;
                    return true;
                }
                var v = Var(name);
                if (v == null || Json.Bool(v, "useVariable")) return false;
                v["value"] = value;
                return true;
            }

            public bool SetVector3(string name, UnityEngine.Vector3 value)
            {
                int p = Param(name);
                if (p < 0) return false;
                if (Kind(p) == "Vector3")
                {
                    var b = Bytes();
                    Array.Copy(BitConverter.GetBytes(value.x), 0, b, Pos(p), 4);
                    Array.Copy(BitConverter.GetBytes(value.y), 0, b, Pos(p) + 4, 4);
                    Array.Copy(BitConverter.GetBytes(value.z), 0, b, Pos(p) + 8, 4);
                    Data["byteData"] = new Dictionary<string, object> { ["$b64"] = Convert.ToBase64String(b) };
                    return true;
                }
                var v = Var(name);
                if (v == null || Json.Bool(v, "useVariable")) return false;
                v["value"] = new List<object> { (double)value.x, (double)value.y, (double)value.z };
                return true;
            }

            /// True if a GameObject parameter (FsmOwnerDefault) is the FSM's owner.
            public bool TargetsOwner(string name = "gameObject")
            {
                var v = Var(name);
                return v != null && v.ContainsKey("ownerOption") && (int)Json.Num(v, "ownerOption") == 0;
            }

            private byte[] Bytes()
            {
                var raw = Data.TryGetValue("byteData", out var b) ? b : null;
                if (raw is Dictionary<string, object> d && d.TryGetValue("$b64", out var s)) return Convert.FromBase64String((string)s);
                if (raw is List<object> l) return l.Select(x => (byte)Json.ToDouble(x)).ToArray();
                return new byte[0];
            }
        }

        private static readonly string[] Types = ("Integer Boolean Float String Color ObjectReference LayerMask Enum Vector2 Vector3 Vector4 Rect Array Character " +
            "AnimationCurve FsmFloat FsmInt FsmBool FsmString FsmGameObject FsmOwnerDefault FunctionCall FsmAnimationCurve FsmEvent FsmObject FsmColor " +
            "Unsupported GameObject FsmVector3 LayoutOption FsmRect FsmEventTarget FsmMaterial FsmTexture Quaternion FsmQuaternion FsmProperty FsmVector2 " +
            "FsmTemplateControl FsmVar CustomClass FsmArray FsmEnum").Split(' ');

        private static readonly Dictionary<string, string> Lists = new Dictionary<string, string>
        {
            ["FsmFloat"] = "fsmFloatParams", ["FsmInt"] = "fsmIntParams", ["FsmBool"] = "fsmBoolParams", ["FsmString"] = "fsmStringParams",
            ["FsmGameObject"] = "fsmGameObjectParams", ["FsmOwnerDefault"] = "fsmOwnerDefaultParams", ["FsmVector3"] = "fsmVector3Params",
            ["FsmVector2"] = "fsmVector2Params", ["FsmColor"] = "fsmColorParams", ["FsmObject"] = "fsmObjectParams", ["FsmMaterial"] = "fsmObjectParams",
            ["FsmTexture"] = "fsmObjectParams", ["FsmEnum"] = "fsmEnumParams", ["FsmArray"] = "fsmArrayParams", ["FsmQuaternion"] = "fsmQuaternionParams",
            ["FsmRect"] = "fsmRectParams", ["FsmVar"] = "fsmVarParams", ["FsmProperty"] = "fsmPropertyParams", ["FsmEventTarget"] = "fsmEventTargetParams",
        };

        private static List<object> L(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is List<object> l ? l : new List<object>();

        /// Every action of the FSMs on a snapshot node (optionally only those of one FSM).
        public static IEnumerable<Action> Actions(Dictionary<string, object> node, string fsmName = null)
        {
            foreach (var ce in Json.Arr(node, "components") ?? new List<object>())
            {
                var c = ce as Dictionary<string, object>;
                if (!(Json.Str(c, "$type") ?? "").StartsWith("PlayMakerFSM")) continue;
                var fsm = Json.Obj(Json.Obj(c, "fields"), "fsm");
                if (fsm == null) continue;
                string name = Json.Str(fsm, "name");
                if (fsmName != null && name != fsmName) continue;
                foreach (var se in Json.Arr(fsm, "states") ?? new List<object>())
                {
                    var data = Json.Obj(se, "actionData");
                    if (data == null) continue;
                    var names = L(data, "actionNames");
                    var starts = L(data, "actionStartIndex");
                    int pcount = L(data, "paramName").Count;
                    for (int a = 0; a < names.Count; a++)
                    {
                        int first = a < starts.Count ? (int)Json.ToDouble(starts[a]) : pcount;
                        int end = a + 1 < starts.Count ? (int)Json.ToDouble(starts[a + 1]) : pcount;
                        string full = (string)names[a];
                        yield return new Action
                        {
                            Fsm = fsm, FsmName = name, State = Json.Str(se, "name"), Index = a, Data = data, First = first, End = end,
                            Type = full.Substring(full.LastIndexOf('.') + 1),
                        };
                    }
                }
            }
        }

        /// A component's variable of an FSM (by kind list name, e.g. "stringVariables") as its saved object.
        public static Dictionary<string, object> Variable(Dictionary<string, object> node, string fsmName, string kind, string varName)
        {
            foreach (var ce in Json.Arr(node, "components") ?? new List<object>())
            {
                var fsm = Json.Obj(Json.Obj(ce, "fields"), "fsm");
                if (fsm == null || Json.Str(fsm, "name") != fsmName) continue;
                foreach (var v in Json.Arr(Json.Obj(fsm, "variables"), kind) ?? new List<object>())
                    if (Json.Str(v, "name") == varName) return v as Dictionary<string, object>;
            }
            return null;
        }
    }
}
