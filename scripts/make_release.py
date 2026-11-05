#!/usr/bin/env python3
"""Builds both release archives into dist/:

    python3 scripts/make_release.py <Apocalypter folder with BepInEx installed>

  dist/Apocars-<version>.zip                 the mod (extract into BepInEx/plugins)
  dist/Apocars-Blender-addon-<version>.zip   the Blender add-on (Install from Disk)

Needs the .NET SDK. The add-on carries a standalone build of the runtime (the one its Build mod button turns into a
mod of its own), so the runtime is built twice: as Apocars and as the standalone."""
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNTIME = os.path.join(ROOT, "runtime")
ADDON = os.path.join(ROOT, "blender", "apocars")
DIST = os.path.join(ROOT, "dist")


def build(game, standalone):
    cmd = ["dotnet", "build", "-c", "Release", "-p:GameDir=" + game]
    if standalone:
        cmd.append("-p:Standalone=true")
    subprocess.check_call(cmd, cwd=RUNTIME)


def zip_dir(src, zpath, arc_root, skip=()):
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for d, dirs, files in os.walk(src):
            dirs[:] = [x for x in dirs if x not in ("__pycache__", "site")]
            for f in files:
                if f.endswith(".pyc") or f in skip:
                    continue
                full = os.path.join(d, f)
                z.write(full, os.path.join(arc_root, os.path.relpath(full, src)))


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    game = os.path.abspath(sys.argv[1])
    version = re.search(r'Version = "([^"]+)"', open(os.path.join(RUNTIME, "Plugin.cs")).read()).group(1)
    addon_version = re.search(r'^version = "([^"]+)"', open(os.path.join(ADDON, "blender_manifest.toml")).read(), re.M).group(1)
    build(game, False)
    build(game, True)
    shutil.copy2(os.path.join(RUNTIME, "bin", "standalone", "ApocarsMod_QQQQQQQQQQQQQQQQ.dll"),
                 os.path.join(ADDON, "runtime", "ApocarsMod.dll"))
    os.makedirs(DIST, exist_ok=True)
    # the mod
    stage = os.path.join(DIST, "Apocars")
    shutil.rmtree(stage, ignore_errors=True)
    os.makedirs(stage)
    shutil.copy2(os.path.join(RUNTIME, "bin", "Release", "Apocars.dll"), stage)
    for f in ("lib/MoonSharp.Interpreter.dll", "lib/MoonSharp.LICENSE.txt", "README.txt"):
        shutil.copy2(os.path.join(RUNTIME, f), stage)
    shutil.copy2(os.path.join(ROOT, "icon.png"), stage)
    mod_zip = os.path.join(DIST, f"Apocars-{version}.zip")
    zip_dir(stage, mod_zip, "Apocars")
    shutil.rmtree(stage)
    # the add-on
    addon_zip = os.path.join(DIST, f"Apocars-Blender-addon-{addon_version}.zip")
    zip_dir(ADDON, addon_zip, "apocars")
    print(mod_zip)
    print(addon_zip)


if __name__ == "__main__":
    main()
