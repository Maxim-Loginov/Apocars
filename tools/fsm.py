#!/usr/bin/env python3
"""Reads PlayMaker FSMs out of an Apocars vanilla dump, as text.

  fsm.py <dump.json> [object path or name] [fsm name]     print FSMs (all, an object's, or one)
  fsm.py <dump.json> --tree                                the object tree with each object's FSM names

Actions are decoded from PlayMaker's ActionData (the form Unity saves them in): each parameter with its value,
FsmX variables as {name}, object references as the path or asset they point at.
"""
import base64
import json
import struct
import sys

TYPES = ("Integer Boolean Float String Color ObjectReference LayerMask Enum Vector2 Vector3 Vector4 Rect Array Character "
         "AnimationCurve FsmFloat FsmInt FsmBool FsmString FsmGameObject FsmOwnerDefault FunctionCall FsmAnimationCurve "
         "FsmEvent FsmObject FsmColor Unsupported GameObject FsmVector3 LayoutOption FsmRect FsmEventTarget FsmMaterial "
         "FsmTexture Quaternion FsmQuaternion FsmProperty FsmVector2 FsmTemplateControl FsmVar CustomClass FsmArray FsmEnum").split()

LISTS = {
    "FsmFloat": "fsmFloatParams", "FsmInt": "fsmIntParams", "FsmBool": "fsmBoolParams", "FsmString": "fsmStringParams",
    "FsmGameObject": "fsmGameObjectParams", "FsmOwnerDefault": "fsmOwnerDefaultParams", "FunctionCall": "functionCallParams",
    "FsmAnimationCurve": "animationCurveParams", "FsmObject": "fsmObjectParams", "FsmColor": "fsmColorParams",
    "FsmVector3": "fsmVector3Params", "FsmVector2": "fsmVector2Params", "FsmRect": "fsmRectParams",
    "FsmEventTarget": "fsmEventTargetParams", "FsmMaterial": "fsmObjectParams", "FsmTexture": "fsmObjectParams",
    "FsmQuaternion": "fsmQuaternionParams", "FsmProperty": "fsmPropertyParams", "FsmTemplateControl": "fsmTemplateControlParams",
    "FsmVar": "fsmVarParams", "FsmArray": "fsmArrayParams", "FsmEnum": "fsmEnumParams", "LayoutOption": "layoutOptionParams",
    "String": "stringParams", "ObjectReference": "unityObjectParams", "GameObject": "unityObjectParams",
}


class Doc:
    def __init__(self, path):
        self.d = json.load(open(path, encoding="utf-8"))
        self.paths = {}
        self._index(self.d["root"])

    def _index(self, n):
        self.paths[n["path"]] = n
        for c in n["children"]:
            self._index(c)

    def name_of(self, path):
        parts, p = [], path
        while True:
            n = self.paths.get(p)
            parts.insert(0, n["name"] if n else "?")
            if not p:
                break
            p = p.rsplit("/", 1)[0] if "/" in p else ""
        return "/".join(parts[1:]) or parts[0]

    def ref(self, r):
        if r is None:
            return "None"
        if not isinstance(r, dict):
            return repr(r)
        if r.get("$ref") == "local":
            t = r.get("type", "")
            return f"<{self.name_of(r['path'])}" + ("" if t == "GameObject" else f":{t.split('.')[-1]}") + ">"
        if r.get("$ref") == "asset":
            return f"[{r['key']}]"
        return repr(r)

    def fsmvar(self, v, kind=""):
        if v is None:
            return "None"
        if isinstance(v, dict):
            if v.get("useVariable") and v.get("name"):
                return "{" + v["name"] + "}"
            if "value" in v:
                val = v["value"]
                if isinstance(val, dict) and "$ref" in val:
                    return self.ref(val)
                return json.dumps(val) if not isinstance(val, str) else repr(val)
            if "ownerOption" in v:
                return "Owner" if v["ownerOption"] == 0 else self.fsmvar(v.get("gameObject"))
            if "target" in v and "excludeSelf" in v:   # FsmEventTarget
                return f"target={v['target']} go={self.fsmvar(v.get('gameObject'))} fsm={self.fsmvar(v.get('fsmName'))}"
            if "TargetObject" in v or "PropertyName" in v:
                return f"property {v.get('PropertyName')} of {self.fsmvar(v.get('TargetObject'))}"
            if "FunctionName" in v:
                return f"call {v.get('FunctionName')}"
            if "variableName" in v:   # FsmVar
                return "{" + v["variableName"] + "}" if v.get("variableName") else json.dumps({k: x for k, x in v.items() if k.endswith("Value") and x})[:120]
            if "$ref" in v:
                return self.ref(v)
            return json.dumps(v)[:160]
        return json.dumps(v)

    def actions(self, ad):
        names = ad.get("actionNames") or []
        starts = ad.get("actionStartIndex") or []
        pnames = ad.get("paramName") or []
        ptypes = ad.get("paramDataType") or []
        ppos = ad.get("paramDataPos") or []
        psize = ad.get("paramByteDataSize") or []
        raw = ad.get("byteData")
        data = base64.b64decode(raw["$b64"]) if isinstance(raw, dict) else bytes(raw or [])
        enabled = ad.get("actionEnabled") or []
        out = []
        for a, full in enumerate(names):
            lo = starts[a] if a < len(starts) else len(pnames)
            hi = starts[a + 1] if a + 1 < len(starts) else len(pnames)
            params = []
            for i in range(lo, hi):
                t = TYPES[ptypes[i]] if ptypes[i] < len(TYPES) else str(ptypes[i])
                params.append((pnames[i], t, self.param(ad, t, ppos[i], psize[i] if i < len(psize) else 0, data)))
            out.append((full.split(".")[-1], enabled[a] if a < len(enabled) else True, params))
        return out

    def param(self, ad, t, pos, size, data):
        try:
            if t in ("Integer", "Enum", "LayerMask"):
                return struct.unpack_from("<i", data, pos)[0]
            if t == "Boolean":
                return bool(data[pos])
            if t == "Float":
                return round(struct.unpack_from("<f", data, pos)[0], 4)
            if t == "Vector3":
                return [round(x, 4) for x in struct.unpack_from("<3f", data, pos)]
            if t == "Vector2":
                return [round(x, 4) for x in struct.unpack_from("<2f", data, pos)]
            if t in ("Color", "Vector4", "Quaternion", "Rect"):
                return [round(x, 4) for x in struct.unpack_from("<4f", data, pos)]
            if t == "FsmEvent":   # the event's name, in stringParams ('' = none)
                sp = ad.get("stringParams") or []
                name = sp[pos] if 0 <= pos < len(sp) else None
                return f"->{name}" if name else "None"
            if t == "Array":
                return f"array[{size}]"
            lst = LISTS.get(t)
            if lst:
                vals = ad.get(lst) or []
                v = vals[pos] if 0 <= pos < len(vals) else None
                if t in ("ObjectReference", "GameObject"):
                    return self.ref(v)
                if t == "String":
                    return repr(v)
                return self.fsmvar(v, t)
        except Exception as e:
            return f"<{t}: {e}>"
        return f"<{t}>"

    def print_fsm(self, node, comp):
        f = comp["fields"]["fsm"]
        print(f"== {self.name_of(node['path']) or node['name']} :: FSM '{f.get('name')}' (start: {f.get('startState')})")
        v = f.get("variables") or {}
        for kind in ("floatVariables", "intVariables", "boolVariables", "stringVariables", "vector2Variables", "vector3Variables",
                     "colorVariables", "rectVariables", "quaternionVariables", "gameObjectVariables", "objectVariables",
                     "materialVariables", "textureVariables", "arrayVariables", "enumVariables"):
            for var in v.get(kind) or []:
                val = var.get("value")
                if isinstance(val, dict) and "$ref" in val:
                    val = self.ref(val)
                print(f"   var {kind[:-9]} {var.get('name')} = {json.dumps(val) if not isinstance(val, str) or kind != 'gameObjectVariables' else val}")
        for e in f.get("events") or []:
            if e.get("isGlobal"):
                print(f"   global event {e.get('name')}")
        for g in f.get("globalTransitions") or []:
            print(f"   global {(g.get('fsmEvent') or {}).get('name')} -> {g.get('toState')}")
        for s in f.get("states") or []:
            tr = ", ".join(f"{(t.get('fsmEvent') or {}).get('name')}->{t.get('toState')}" for t in s.get("transitions") or [])
            print(f"   [{s.get('name')}] {tr}")
            for name, on, params in self.actions(s.get("actionData") or {}):
                ps = ", ".join(f"{p}={v}" for p, t, v in params)
                print(f"      {'' if on else '(off) '}{name}({ps})")


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    doc = Doc(sys.argv[1])
    if len(sys.argv) > 2 and sys.argv[2] == "--tree":
        def tree(n, depth=0):
            fsms = [c["fields"]["fsm"]["name"] for c in n["components"] if c["$type"].startswith("PlayMakerFSM")]
            print("  " * depth + n["name"] + (": " + ", ".join(fsms) if fsms else ""))
            for c in n["children"]:
                tree(c, depth + 1)
        tree(doc.d["root"])
        return
    want_obj = sys.argv[2] if len(sys.argv) > 2 else None
    want_fsm = sys.argv[3] if len(sys.argv) > 3 else None
    for path, node in doc.paths.items():
        full = doc.name_of(path)
        if want_obj and want_obj not in (node["name"], full, path):
            continue
        for c in node["components"]:
            if c["$type"].startswith("PlayMakerFSM") and "fields" in c:
                if want_fsm and c["fields"]["fsm"].get("name") != want_fsm:
                    continue
                doc.print_fsm(node, c)


if __name__ == "__main__":
    main()
