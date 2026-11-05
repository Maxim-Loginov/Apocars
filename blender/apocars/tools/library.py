#!/usr/bin/env python3
"""The vanilla library the Blender add-on previews parts from ("test"), read from the game's own files:

    python3 library.py <Apocalypter_Data folder or data.unity3d> <Apocars-vanilla dump folder> <out folder> [--max-texture 512]

Writes <out>/<part>.glb for every vanilla car part of the dump (its shown meshes, base colours and main textures, its
hierarchy as the game has it), <out>/index.json (each part's ID, name, size and file) and <out>/materials.json (every
game material: name, colour, texture), for the add-on's vanilla material picker, and <out>/mounts/*.glb: the meshes
the game's cars carry on their hinges and controls (engine and radiator mounts, the exhaust bracket, wheel hubs, the
steering column, the switch panel), each in its own object's space, for the add-on to show on attachment points.
Needs UnityPy (pip install UnityPy). Unity is left-handed, glTF right-handed: X is mirrored, as Apocars mirrors it back."""
import io
import json
import os
import struct
import sys
import time

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler


def deref(pptr):
    if pptr is None or getattr(pptr, "m_PathID", 0) == 0:
        return None
    for fn in ("deref_parse_as_object", "read"):
        f = getattr(pptr, fn, None)
        if f:
            try:
                return f()
            except Exception:
                pass
    return None


def key(pptr):
    try:
        return (pptr.assets_file.name if hasattr(pptr, "assets_file") else "", pptr.m_FileID, pptr.m_PathID)
    except Exception:
        return id(pptr)


def components(go):
    for c in getattr(go, "m_Component", []) or []:
        yield getattr(c, "component", None) or (c[1] if isinstance(c, (tuple, list)) else c)


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


class Glb:
    def __init__(self):
        self.j = {"asset": {"version": "2.0", "generator": "Apocars library"}, "scene": 0, "scenes": [{"nodes": []}],
                  "nodes": [], "meshes": [], "materials": [], "textures": [], "images": [], "accessors": [], "bufferViews": [],
                  "buffers": [], "samplers": [{"wrapS": 10497, "wrapT": 10497}]}
        self.bin = bytearray()

    def view(self, data, target=None):
        while len(self.bin) % 4:
            self.bin.append(0)
        v = {"buffer": 0, "byteOffset": len(self.bin), "byteLength": len(data)}
        if target:
            v["target"] = target
        self.bin += data
        self.j["bufferViews"].append(v)
        return len(self.j["bufferViews"]) - 1

    def accessor(self, values, comps, ctype=5126, target=34962, minmax=False):
        flat = [x for v in values for x in (v if comps > 1 else (v,))]
        fmt = {5126: "f", 5125: "I"}[ctype]
        a = {"bufferView": self.view(struct.pack(f"<{len(flat)}{fmt}", *flat), target), "componentType": ctype,
             "count": len(values), "type": {1: "SCALAR", 2: "VEC2", 3: "VEC3", 4: "VEC4"}[comps]}
        if minmax:
            a["min"] = [min(v[i] for v in values) for i in range(comps)]
            a["max"] = [max(v[i] for v in values) for i in range(comps)]
        self.j["accessors"].append(a)
        return len(self.j["accessors"]) - 1

    def save(self, path):
        for k in ("meshes", "materials", "textures", "images", "accessors", "bufferViews"):
            if not self.j[k]:
                del self.j[k]
        if "textures" not in self.j:
            self.j.pop("samplers", None)
        while len(self.bin) % 4:
            self.bin.append(0)
        self.j["buffers"] = [{"byteLength": len(self.bin)}] if self.bin else []
        if not self.bin:
            del self.j["buffers"]
        js = json.dumps(self.j, separators=(",", ":")).encode()
        js += b" " * (-len(js) % 4)
        out = struct.pack("<4sII", b"glTF", 2, 12 + 8 + len(js) + (8 + len(self.bin) if self.bin else 0))
        out += struct.pack("<II", len(js), 0x4E4F534A) + js
        if self.bin:
            out += struct.pack("<II", len(self.bin), 0x004E4942) + bytes(self.bin)
        with open(path, "wb") as f:
            f.write(out)


class Exporter:
    def __init__(self, max_tex):
        self.max_tex = max_tex
        self.mesh_cache = {}      # unity mesh key -> (glTF primitives data)

    # ---- one prefab
    def export(self, root_go, path):
        g = Glb()
        mats, texs = {}, {}

        def material(mp):
            k = key(mp)
            if k in mats:
                return mats[k]
            m = deref(mp)
            entry = {"name": getattr(m, "m_Name", "material"), "pbrMetallicRoughness": {"metallicFactor": 0.0, "roughnessFactor": 0.8}}
            if m is not None:
                props = m.m_SavedProperties
                for name, col in props.m_Colors:
                    if name == "_Color":
                        entry["pbrMetallicRoughness"]["baseColorFactor"] = [srgb_to_linear(col.r), srgb_to_linear(col.g), srgb_to_linear(col.b), col.a]
                for name, env in props.m_TexEnvs:
                    if name != "_MainTex":
                        continue
                    t = texture(env.m_Texture)
                    if t is not None:
                        entry["pbrMetallicRoughness"]["baseColorTexture"] = {"index": t}
                for name, f in props.m_Floats:
                    if name == "_Glossiness":
                        entry["pbrMetallicRoughness"]["roughnessFactor"] = max(0.0, 1.0 - f)
                    if name == "_Metallic":
                        entry["pbrMetallicRoughness"]["metallicFactor"] = f
                entry["extras"] = {"vanilla": entry["name"]}   # the game's material: Apocars uses it as it is
            g.j["materials"].append(entry)
            mats[k] = len(g.j["materials"]) - 1
            return mats[k]

        def texture(tp):
            k = key(tp)
            if k in texs:
                return texs[k]
            texs[k] = None
            t = deref(tp)
            if t is None:
                return None
            try:
                img = t.image
                if self.max_tex and max(img.size) > self.max_tex:
                    s = self.max_tex / max(img.size)
                    img = img.resize((max(1, int(img.size[0] * s)), max(1, int(img.size[1] * s))))
                buf = io.BytesIO()
                img.save(buf, "PNG")
            except Exception:
                return None
            g.j["images"].append({"bufferView": g.view(buf.getvalue()), "mimeType": "image/png", "name": t.m_Name})
            g.j["textures"].append({"source": len(g.j["images"]) - 1, "sampler": 0})
            texs[k] = len(g.j["textures"]) - 1
            return texs[k]

        def mesh(mp, materials):
            m = deref(mp)
            if m is None:
                return None
            h = MeshHandler(m)
            h.process()
            v = h.m_Vertices or []
            n = len(v)
            if n == 0:
                return None
            pos = [(-p[0], p[1], p[2]) for p in v]
            nrm = h.m_Normals if h.m_Normals is not None and len(h.m_Normals) == n else None
            uv = h.m_UV0 if h.m_UV0 is not None and len(h.m_UV0) == n else None
            attrs = {"POSITION": g.accessor(pos, 3, minmax=True)}
            if nrm is not None:
                attrs["NORMAL"] = g.accessor([(-p[0], p[1], p[2]) for p in nrm], 3)
            if uv is not None:
                attrs["TEXCOORD_0"] = g.accessor([(p[0], 1.0 - p[1]) for p in uv], 2)
            prims = []
            tris = h.get_triangles()
            subs = m.m_SubMeshes
            for s, tl in enumerate(tris):
                base = getattr(subs[s], "baseVertex", 0) or 0
                idx = [i for t in tl for i in (t[0] + base, t[2] + base, t[1] + base)]   # mirrored: triangles turn round
                if not idx:
                    continue
                p = {"attributes": attrs, "indices": g.accessor(idx, 1, 5125, 34963)}
                if s < len(materials) and materials[s] is not None:
                    p["material"] = material(materials[s])
                prims.append(p)
            if not prims:
                return None
            g.j["meshes"].append({"name": m.m_Name, "primitives": prims})
            return len(g.j["meshes"]) - 1

        def node(tr, top):
            go = deref(tr.m_GameObject)
            if go is None or (not top and not getattr(go, "m_IsActive", True)):
                return None
            lp, lr, ls = tr.m_LocalPosition, tr.m_LocalRotation, tr.m_LocalScale
            nd = {"name": go.m_Name}
            if not top:   # the part's own origin is the file's
                nd["translation"] = [-lp.x, lp.y, lp.z]
                nd["rotation"] = [lr.x, -lr.y, -lr.z, lr.w]
                nd["scale"] = [ls.x, ls.y, ls.z]
            mf = mr = None
            for cp in components(go):
                c = deref(cp)
                if c is None:
                    continue
                t = type(c).__name__
                if t == "MeshFilter":
                    mf = c
                elif t == "MeshRenderer":
                    mr = c
            if mf is not None and mr is not None and getattr(mr, "m_Enabled", True):
                try:
                    mi = mesh(mf.m_Mesh, list(mr.m_Materials or []))
                except Exception as e:
                    mi = None
                    print(f"    mesh of {go.m_Name}: {e}", flush=True)
                if mi is not None:
                    nd["mesh"] = mi
            g.j["nodes"].append(nd)
            me = len(g.j["nodes"]) - 1
            kids = []
            for ch in tr.m_Children or []:
                ct = deref(ch)
                if ct is None:
                    continue
                k = node(ct, False)
                if k is not None:
                    kids.append(k)
            if kids:
                g.j["nodes"][me]["children"] = kids
            return me

        tr = None
        for cp in components(root_go):
            c = deref(cp)
            if c is not None and type(c).__name__ in ("Transform", "RectTransform"):
                tr = c
                break
        if tr is None:
            return False
        g.j["scenes"][0]["nodes"] = [node(tr, True)]
        g.save(path)
        return True


# what the add-on shows on attachment points: the vanilla object Apocars builds that point from (car, path under its root)
MOUNTS = {
    "engine": ("Junker", "hinge_engine"),
    "radiator": ("Poloska", "hinge_radiator"),
    "exhaust": ("Junker", "hinge_exhaust"),
    "wheel_FL": ("Poloska", "hinge_wheel_FL"),
    "wheel_FR": ("Poloska", "hinge_wheel_FR"),
    "wheel_RL": ("Poloska", "hinge_wheel_RL"),
    "wheel_RR": ("Poloska", "hinge_wheel_RR"),
    "steering_column": ("Poloska", "parts/steering rod"),
    "switch_panel": ("Poloska", "switch_panel"),
    "fuel_tank": ("Poloska", "parts/fueltank"),
    "fuel_cap": ("Poloska", "Fuel"),
    "handbrake": ("Poloska", "handbrake"),
    "toolbox": ("Poloska", "parts/toolset_1"),
    "adjuster": ("Poloska", "parts/toolset_2"),
}

# decorative pieces of the game's cars the add-on can import: (category, name pattern) on the objects under a car's
# "parts" (and each suspension variant under "suspension_model" as one piece)
import re as _re
DECOR_RULES = [
    ("Suspension", _re.compile(r"suspension|spring|wheel_holder|wheel cover|undershield")),
    ("Drivetrain", _re.compile(r"drive shaft|gearlever_bottom|gearlever holder|^gear$|chassis")),
    ("Engine bay", _re.compile(r"engine.?bay|firewall|battery|wiring|^wire$|cable")),
    ("Fuel tank", _re.compile(r"fuel.?tank|^tank_metal$")),
]
DECOR_SKIP = _re.compile(r"PlayerModel_Sit|hingeAdjustVisibility|toolset|pedals|colliders|interior_car_light")


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def qrot(q, v):
    x, y, z, w = q
    p = qmul(qmul(q, (v[0], v[1], v[2], 0.0)), (-x, -y, -z, w))
    return p[:3]


def root_relative(tr):
    """A transform's position and rotation relative to its root (Unity axes)."""
    chain = []
    while tr is not None and getattr(tr.m_Father, "m_PathID", 0):
        chain.insert(0, tr)
        tr = deref(tr.m_Father)
    pos, rot = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)
    for t in chain:
        lp, lr = t.m_LocalPosition, t.m_LocalRotation
        d = qrot(rot, (lp.x, lp.y, lp.z))
        pos = (pos[0] + d[0], pos[1] + d[1], pos[2] + d[2])
        rot = qmul(rot, (lr.x, lr.y, lr.z, lr.w))
    return [round(x, 5) for x in pos], [round(x, 7) for x in rot]


def children(tr):
    for ch in tr.m_Children or []:
        ct = deref(ch)
        go = deref(ct.m_GameObject) if ct is not None else None
        if go is not None:
            yield ct, go


def has_mesh(go):
    for cp in components(go):
        c = deref(cp)
        if c is not None and type(c).__name__ == "MeshFilter":
            return True
    return False


def decor_items(car, root_tr):
    """(category, label, transform) of a car's decorative pieces."""
    out = []
    for top, tgo in children(root_tr):
        if tgo.m_Name == "suspension_model":
            for var, vgo in children(top):
                out.append(("Suspension", f"{var_label(vgo.m_Name)} suspension", var))
        elif tgo.m_Name == "parts":
            def walk(tr, path):
                for ct, go in children(tr):
                    p = path + "/" + go.m_Name
                    if DECOR_SKIP.search(p):
                        continue
                    name = go.m_Name.lower()
                    cat = next((c for c, rx in DECOR_RULES if rx.search(name)), None)
                    if cat and (has_mesh(go) or any(True for _ in children(ct))):
                        out.append((cat, go.m_Name, ct))   # with what's under it
                        continue
                    walk(ct, p)
            walk(top, "parts")
    return out


def var_label(n):
    return {"stock": "Stock", "lifted": "Lifted", "buggy": "Buggy"}.get(n, n)


def child_path(tr, path):
    for name in path.split("/"):
        nxt = None
        for ch in tr.m_Children or []:
            ct = deref(ch)
            go = deref(ct.m_GameObject) if ct is not None else None
            if go is not None and go.m_Name == name:
                nxt = ct
                break
        if nxt is None:
            return None
        tr = nxt
    return tr


def part_info(dump_file):
    d = json.load(open(dump_file, encoding="utf-8"))
    root = d["root"]
    info = {"id": None, "name": None, "children": len(root.get("children", []))}
    for c in root.get("components", []):
        fsm = (c.get("fields") or {}).get("fsm") or {}
        for v in (fsm.get("variables") or {}).get("stringVariables") or []:
            if fsm.get("name") == "ID" and v.get("name") == "ID":
                info["id"] = v.get("value")
            if fsm.get("name") == "ItemName" and v.get("name") == "ItemName":
                info["name"] = v.get("value")
    return d["name"], info


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    max_tex = 512
    if "--max-texture" in sys.argv:
        max_tex = int(sys.argv[sys.argv.index("--max-texture") + 1])
        args = [a for a in args if a != str(max_tex)]
    data, dump, out = args[0], args[1], args[2]
    if os.path.isdir(data):
        data = os.path.join(data, "data.unity3d")
    os.makedirs(out, exist_ok=True)
    t0 = time.time()

    wanted = {}
    for f in sorted(os.listdir(os.path.join(dump, "parts"))):
        if f.endswith(".json"):
            name, info = part_info(os.path.join(dump, "parts", f))
            wanted[name] = info
    print(f"{len(wanted)} vanilla parts in the dump", flush=True)

    env = UnityPy.load(data)
    print(f"loaded {data} in {time.time() - t0:.1f}s", flush=True)
    roots = {}
    materials = {}
    cars = {car for car, _ in MOUNTS.values()}
    vehicles_dir = os.path.join(dump, "vehicles")
    if os.path.isdir(vehicles_dir):
        cars |= {f[:-5] for f in os.listdir(vehicles_dir) if f.endswith(".json")}
    car_roots = {}
    for obj in env.objects:
        tn = obj.type.name
        if tn == "Material":
            try:
                m = obj.read()
                if m.m_Name in materials:
                    continue
                col = next((c for n, c in m.m_SavedProperties.m_Colors if n == "_Color"), None)
                tex = next((env_.m_Texture for n, env_ in m.m_SavedProperties.m_TexEnvs if n == "_MainTex"), None)
                tt = deref(tex) if tex is not None else None
                shader = deref(m.m_Shader)
                tex_file = None
                if tt is not None:
                    tex_file = "materials/" + "".join(c if c not in '<>:"/\\|?*' else "_" for c in tt.m_Name) + ".png"
                    full = os.path.join(out, tex_file)
                    if not os.path.isfile(full):
                        try:
                            img = tt.image
                            if max_tex and max(img.size) > max_tex:
                                sc = max_tex / max(img.size)
                                img = img.resize((max(1, int(img.size[0] * sc)), max(1, int(img.size[1] * sc))))
                            os.makedirs(os.path.dirname(full), exist_ok=True)
                            img.save(full, "PNG")
                        except Exception:
                            tex_file = None
                materials[m.m_Name] = {"texture_file": tex_file, "color": [col.r, col.g, col.b, col.a] if col else [1, 1, 1, 1],
                                       "texture": getattr(tt, "m_Name", None), "shader": getattr(shader, "m_Name", None)}
            except Exception:
                pass
            continue
        if tn != "GameObject":
            continue
        try:
            go = obj.read()
        except Exception:
            continue
        if go.m_Name in cars:
            tr = None
            for cp in components(go):
                c = deref(cp)
                if c is not None and type(c).__name__ == "Transform":
                    tr = c
                    break
            if tr is not None and getattr(tr.m_Father, "m_PathID", 0) == 0:
                found = sum(1 for car, path in MOUNTS.values() if car == go.m_Name and child_path(tr, path) is not None)
                found += sum(1 for _ in children(tr)) * 0.001   # a car not in MOUNTS: the fullest copy
                if go.m_Name not in car_roots or found > car_roots[go.m_Name][0]:
                    car_roots[go.m_Name] = (found, tr)
        if go.m_Name not in wanted:
            continue
        tr = next((deref(cp) for cp in components(go)), None)
        tr = None
        for cp in components(go):
            c = deref(cp)
            if c is not None and type(c).__name__ == "Transform":
                tr = c
                break
        if tr is None or getattr(tr.m_Father, "m_PathID", 0) != 0:
            continue
        # a prefab, not a copy in a scene: the one whose children count matches the dump's, else the first
        score = 1 if len(tr.m_Children or []) == wanted[go.m_Name]["children"] else 0
        if go.m_Name not in roots or score > roots[go.m_Name][0]:
            roots[go.m_Name] = (score, go)
    print(f"found {len(roots)} of them in {time.time() - t0:.1f}s", flush=True)

    ex = Exporter(max_tex)
    index = {}
    for name, (score, go) in sorted(roots.items()):
        safe = "".join(c if c not in '<>:"/\\|?*' else "_" for c in name)
        path = os.path.join(out, safe + ".glb")
        try:
            if ex.export(go, path):
                info = wanted[name]
                index[name] = {"file": safe + ".glb", "id": info["id"], "name": info["name"]}
        except Exception as e:
            print(f"  {name}: {e}", flush=True)
    mounts = {}
    os.makedirs(os.path.join(out, "mounts"), exist_ok=True)
    for key_, (car, path) in sorted(MOUNTS.items()):
        tr = child_path(car_roots[car][1], path) if car in car_roots else None
        go = deref(tr.m_GameObject) if tr is not None else None
        if go is None:
            print(f"  mount {key_}: no {path} on a {car}", flush=True)
            continue
        try:
            if ex.export(go, os.path.join(out, "mounts", key_ + ".glb")):
                mounts[key_] = {"file": "mounts/" + key_ + ".glb", "car": car, "path": path}
        except Exception as e:
            print(f"  mount {key_}: {e}", flush=True)
    json.dump(mounts, open(os.path.join(out, "mounts.json"), "w"), indent=1, sort_keys=True)

    # decorative pieces of every car
    decor = []
    for car, (_, root_tr) in sorted(car_roots.items()):
        seen = {}
        for cat, label, tr in decor_items(car, root_tr):
            go = deref(tr.m_GameObject)
            n = seen.get((cat, label), 0) + 1
            seen[(cat, label)] = n
            shown = label if n == 1 else f"{label} {n}"
            safe = "".join(c if c.isalnum() or c in "-_" else "_" for c in f"{car}_{cat}_{shown}")
            path = os.path.join(out, "decor", safe + ".glb")
            os.makedirs(os.path.dirname(path), exist_ok=True)
            try:
                if not ex.export(go, path):
                    continue
            except Exception as e:
                print(f"  decor {car} {label}: {e}", flush=True)
                continue
            pos, rot = root_relative(tr)
            decor.append({"id": f"{car}/{cat}/{shown}", "car": car, "category": cat, "name": shown,
                          "file": "decor/" + safe + ".glb", "pos": pos, "rot": rot})
    json.dump(decor, open(os.path.join(out, "decor.json"), "w"), indent=1)
    json.dump(index, open(os.path.join(out, "index.json"), "w"), indent=1, sort_keys=True)
    json.dump(materials, open(os.path.join(out, "materials.json"), "w"), indent=1, sort_keys=True)
    print(f"wrote {len(index)} parts, {len(mounts)} mounts, {len(decor)} decorative pieces and {len(materials)} materials to {out} in {time.time() - t0:.1f}s", flush=True)


if __name__ == "__main__":
    main()
