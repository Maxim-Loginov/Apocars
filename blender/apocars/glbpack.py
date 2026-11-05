# What an exported .glb keeps of its materials' pictures.
#   A game material (tagged vanilla): nothing. Apocars uses the game's own material, so its pictures stay out.
#   Our own materials: their pictures go into <folder>/textures, each saved once however many files use it (the file
#   name carries a hash of the picture, so two files with the same picture point to the same file), and the .glb
#   refers to it. Pictures no .glb uses any more are removed.
import glob
import hashlib
import json
import os
import re
import struct

TEXTURES = "textures"
EXT = {"image/png": ".png", "image/jpeg": ".jpg", "image/webp": ".webp"}


def _read(path):
    data = open(path, "rb").read()
    if data[:4] != b"glTF":
        raise ValueError(path + ": not a .glb")
    js, bin_ = None, b""
    p = 12
    while p + 8 <= len(data):
        n, kind = struct.unpack_from("<II", data, p)
        chunk = data[p + 8:p + 8 + n]
        if kind == 0x4E4F534A:
            js = json.loads(chunk.decode("utf-8"))
        elif kind == 0x004E4942:
            bin_ = chunk
        p += 8 + n
    return js, bin_


def _write(path, js, bin_):
    j = json.dumps(js, separators=(",", ":")).encode("utf-8")
    j += b" " * (-len(j) % 4)
    bin_ += b"\0" * (-len(bin_) % 4)
    total = 12 + 8 + len(j) + (8 + len(bin_) if bin_ else 0)
    out = [struct.pack("<III", 0x46546C67, 2, total), struct.pack("<II", len(j), 0x4E4F534A), j]
    if bin_:
        out += [struct.pack("<II", len(bin_), 0x004E4942), bin_]
    tmp = path + ".part"
    with open(tmp, "wb") as f:
        f.write(b"".join(out))
    os.replace(tmp, path)       # whole, at once (Apocars may be watching it)


def _texture_infos(node, strip=False):
    """The texture references in a material (keys ending in 'Texture', also inside extensions); strip: removed."""
    found = []
    if isinstance(node, dict):
        for k in list(node):
            v = node[k]
            if k.endswith("Texture") and isinstance(v, dict) and "index" in v:
                if strip:
                    del node[k]
                else:
                    found.append(v)
            else:
                found += _texture_infos(v, strip)
    elif isinstance(node, list):
        for v in node:
            found += _texture_infos(v, strip)
    return found


def _safe(name):
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name or "image").strip("._") or "image"


def pack(src, dst, folder):
    """src: the .glb as Blender wrote it; dst: written with its pictures moved out (or left out). The pictures used."""
    js, bin_ = _read(src)
    mats = js.get("materials", [])
    for m in mats:
        if (m.get("extras") or {}).get("vanilla"):
            _texture_infos(m, strip=True)          # the game's material: its pictures aren't ours to carry
    textures, images, views = js.get("textures", []), js.get("images", []), js.get("bufferViews", [])
    # the textures still used, renumbered
    used_tex = []
    for m in mats:
        for info in _texture_infos(m):
            if info["index"] not in used_tex:
                used_tex.append(info["index"])
    tex_map = {old: new for new, old in enumerate(sorted(used_tex))}
    for m in mats:
        for info in _texture_infos(m):
            info["index"] = tex_map[info["index"]]
    new_textures = [textures[i] for i in sorted(used_tex)]

    def sources(t):
        out = [t] if "source" in t else []
        out += [e for e in (t.get("extensions") or {}).values() if isinstance(e, dict) and "source" in e]
        return out

    used_img = sorted({s["source"] for t in new_textures for s in sources(t)})
    img_map = {old: new for new, old in enumerate(used_img)}
    for t in new_textures:
        for s in sources(t):
            s["source"] = img_map[s["source"]]
    # each picture into textures/, named by what it is (the same picture in two files: one file)
    tex_dir = os.path.join(folder, TEXTURES)
    written, image_views = [], set()
    new_images = []
    for i in used_img:
        img = dict(images[i])
        if "bufferView" in img:
            bv = views[img["bufferView"]]
            image_views.add(img["bufferView"])
            off = bv.get("byteOffset", 0)
            data = bin_[off:off + bv["byteLength"]]
            ext = EXT.get(img.get("mimeType"), ".png")
            fname = f"{_safe(img.get('name'))}_{hashlib.sha1(data).hexdigest()[:10]}{ext}"
            os.makedirs(tex_dir, exist_ok=True)
            f = os.path.join(tex_dir, fname)
            if not (os.path.isfile(f) and os.path.getsize(f) == len(data)):
                with open(f + ".part", "wb") as h:
                    h.write(data)
                os.replace(f + ".part", f)
            img = {k: v for k, v in img.items() if k not in ("bufferView", "mimeType")}
            img["uri"] = TEXTURES + "/" + fname
            written.append(fname)
        new_images.append(img)
    for i, img in enumerate(images):            # pictures dropped altogether: their data goes too
        if i not in img_map and "bufferView" in img:
            image_views.add(img["bufferView"])
    # the binary without the pictures (the rest moved up, kept 4-byte aligned)
    view_map, out = {}, bytearray()
    new_views = []
    for i, bv in enumerate(views):
        if i in image_views:
            continue
        off = bv.get("byteOffset", 0)
        out += b"\0" * (-len(out) % 4)
        nv = dict(bv)
        nv["byteOffset"] = len(out)
        out += bin_[off:off + bv["byteLength"]]
        view_map[i] = len(new_views)
        new_views.append(nv)
    for a in js.get("accessors", []):
        if "bufferView" in a:
            a["bufferView"] = view_map[a["bufferView"]]
        sp = a.get("sparse")
        if sp:
            sp["indices"]["bufferView"] = view_map[sp["indices"]["bufferView"]]
            sp["values"]["bufferView"] = view_map[sp["values"]["bufferView"]]
    for key, val in (("textures", new_textures), ("images", new_images), ("bufferViews", new_views)):
        if val:
            js[key] = val
        else:
            js.pop(key, None)
    if not new_textures:
        js.pop("samplers", None)
    else:
        used_s = sorted({t["sampler"] for t in new_textures if "sampler" in t})
        s_map = {old: new for new, old in enumerate(used_s)}
        js["samplers"] = [js["samplers"][i] for i in used_s]
        for t in new_textures:
            if "sampler" in t:
                t["sampler"] = s_map[t["sampler"]]
    out += b"\0" * (-len(out) % 4)
    if js.get("buffers"):
        js["buffers"][0]["byteLength"] = len(out)
        if not out:
            js.pop("buffers")
    _write(dst, js, bytes(out))
    return written


def prune(folder):
    """Pictures in textures/ that no .glb in the folder uses any more go."""
    tex_dir = os.path.join(folder, TEXTURES)
    if not os.path.isdir(tex_dir):
        return 0
    used = set()
    for g in glob.glob(os.path.join(folder, "*.glb")):
        try:
            js, _ = _read(g)
        except Exception:
            return 0                            # a file we can't read might use any of them: keep all
        for img in js.get("images", []):
            u = img.get("uri", "")
            if u.startswith(TEXTURES + "/"):
                used.add(u[len(TEXTURES) + 1:])
    n = 0
    for f in os.listdir(tex_dir):
        if f not in used:
            try:
                os.remove(os.path.join(tex_dir, f))
                n += 1
            except OSError:
                pass
    return n


def export_glb(glb, folder, **kwargs):
    """Blender's glTF export into a temporary file, then packed into glb (Apocars sees one finished file)."""
    import tempfile
    import bpy
    tmp = os.path.join(tempfile.mkdtemp(prefix="apocars_glb_"), os.path.basename(glb))
    try:
        bpy.ops.export_scene.gltf(filepath=tmp, export_format="GLB", **kwargs)
        return pack(tmp, glb, folder)
    finally:
        try:
            os.remove(tmp)
            os.rmdir(os.path.dirname(tmp))
        except OSError:
            pass
