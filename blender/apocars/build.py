# Build: the asset as a BepInEx mod of its own, which works without Apocars installed. The mod is Apocars' runtime
# (a copy of its standalone build, bundled with this add-on) given its own identity, reading only its own folder:
#   BepInEx/plugins/<Mod>/<Mod>.dll, MoonSharp.Interpreter.dll, apocars.standalone, apocars/<id>.glb + <id>.lua
# The runtime's placeholders are filled in byte for byte (same lengths): no compiler needed. A zip of the mod, to
# share, goes next to the .blend.
import hashlib
import os
import re
import shutil
import zipfile

import bpy

from . import export, game
from .ops import S

HERE = os.path.dirname(os.path.abspath(__file__))
RUNTIME = os.path.join(HERE, "runtime")
TEMPLATE = "ApocarsMod.dll"
HASH_TOKEN = "Q" * 16
NAME_TOKEN = "N" * 40
MARKER = "apocars.standalone"


class BuildError(Exception):
    pass


def safe_name(name):
    n = re.sub(r"[^A-Za-z0-9_]", "", (name or "").replace(" ", "_"))
    return n or "MyApocars"


def patch(data, mod):
    """The runtime with the mod's identity: its plugin GUID and assembly name end in a hash of its name, its name shown."""
    h = hashlib.sha1(mod.lower().encode()).hexdigest()[:16].upper()
    shown = (mod[:40]).ljust(40)
    for token, value in ((HASH_TOKEN, h), (NAME_TOKEN, shown)):
        for enc in ("utf-8", "utf-16-le"):
            t, v = token.encode(enc), value.encode(enc)
            if data.count(t) == 0:
                raise BuildError(f"the bundled runtime has no {token[:4]}... placeholder ({enc}): reinstall the add-on")
            data = data.replace(t, v)
    return data, h


def build(context, game_folder):
    s = S(context)
    mod = safe_name(s.mod_name)
    src = os.path.join(RUNTIME, TEMPLATE)
    if not os.path.isfile(src):
        raise BuildError("the add-on's runtime is missing (runtime/ApocarsMod.dll): reinstall the add-on")
    plugins = os.path.join(game.bepinex(game_folder), "plugins")
    folder = os.path.join(plugins, mod)
    os.makedirs(folder, exist_ok=True)

    data, h = patch(open(src, "rb").read(), mod)
    with open(os.path.join(folder, mod + ".dll"), "wb") as f:
        f.write(data)
    for extra in ("MoonSharp.Interpreter.dll", "MoonSharp.LICENSE.txt"):
        if os.path.isfile(os.path.join(RUNTIME, extra)):
            shutil.copy2(os.path.join(RUNTIME, extra), os.path.join(folder, extra))
    # the Apocars icon, unless the mod has one of its own already
    icon = os.path.join(RUNTIME, "icon.png")
    if os.path.isfile(icon) and not os.path.isfile(os.path.join(folder, "icon.png")):
        shutil.copy2(icon, os.path.join(folder, "icon.png"))
    with open(os.path.join(folder, MARKER), "w", encoding="utf-8") as f:
        f.write(f"Built by the Apocars Blender add-on. Plugin GUID com.apocalypter.apocars.mod.{h}.\n"
                "Apocars (the lib) leaves this folder to this mod.\n")
    export.write(context, os.path.join(folder, "apocars"))
    with open(os.path.join(folder, "README.txt"), "w", encoding="utf-8") as f:
        f.write(f"{mod}: made with Apocars. Install: unzip into Apocalypter/BepInEx/plugins (BepInEx 5 needed, Apocars not).\n"
                f"Assets: {', '.join(sorted(n[:-4] for n in os.listdir(os.path.join(folder, 'apocars')) if n.endswith('.lua')))}\n")

    # the mod as a zip to share, next to the .blend (or the mod folder if the .blend isn't saved)
    where = os.path.dirname(bpy.data.filepath) if bpy.data.filepath else plugins
    zpath = os.path.join(where, mod + ".zip")
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for d, _, files in os.walk(folder):
            for name in files:
                full = os.path.join(d, name)
                z.write(full, os.path.join(mod, os.path.relpath(full, folder)))
    return f"{folder} (and {zpath})"
