using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MoonSharp.Interpreter;
using UnityEngine;

namespace Apocars
{
    /// An asset's description: the .lua next to its .glb, which returns a table (what the Blender add-on writes;
    /// it can be edited by hand). Read into plain dictionaries and lists.
    internal static class Spec
    {
        public static Dictionary<string, object> Load(string path)
        {
            var script = new Script(CoreModules.Preset_SoftSandbox);
            script.Globals["log"] = (Action<string>)(m => Plugin.Log.LogInfo($"[{Path.GetFileName(path)}] {m}"));
            DynValue result;
            try { result = script.DoString(File.ReadAllText(path), null, Path.GetFileName(path)); }
            catch (InterpreterException e) { throw new FormatException(e.DecoratedMessage ?? e.Message); }
            if (result.Type != DataType.Table) throw new FormatException("the script must return a table: return { kind = \"vehicle\", ... }");
            return (Dictionary<string, object>)Plain(result);
        }

        private static object Plain(DynValue v)
        {
            switch (v.Type)
            {
                case DataType.Nil: case DataType.Void: return null;
                case DataType.Boolean: return v.Boolean;
                case DataType.Number: return v.Number;
                case DataType.String: return v.String;
                case DataType.Table:
                {
                    var t = v.Table;
                    int n = t.Length;
                    bool isList = n > 0 && t.Pairs.All(p => p.Key.Type == DataType.Number);
                    if (isList)
                    {
                        var list = new List<object>(n);
                        for (int i = 1; i <= n; i++) list.Add(Plain(t.Get(i)));
                        return list;
                    }
                    var d = new Dictionary<string, object>();
                    foreach (var p in t.Pairs) d[p.Key.Type == DataType.String ? p.Key.String : p.Key.ToPrintString()] = Plain(p.Value);
                    return d;
                }
                default: return v.ToPrintString();
            }
        }

        // ------------------------------------------------------------------ reading values

        public static Vector3 Vec(object raw, Vector3 def)
        {
            if (raw is List<object> l && l.Count >= 3) return new Vector3((float)Json.ToDouble(l[0]), (float)Json.ToDouble(l[1]), (float)Json.ToDouble(l[2]));
            return def;
        }

        public static List<Dictionary<string, object>> Entries(Dictionary<string, object> spec, string key) =>
            (Json.Arr(spec, key) ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
    }
}
