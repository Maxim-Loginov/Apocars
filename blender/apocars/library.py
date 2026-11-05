# The vanilla library: the game's car parts as .glb files the "test" button shows (BepInEx/Apocars-library), made from
# the game's own files by tools/library.py (UnityPy, installed into the add-on's own folder the first time) and the
# Apocars vanilla dump (Apocars' Tools.DumpVanilla, in a loaded game).
import os
import subprocess
import sys

import bpy

from . import game
from .ops import S, game_dir

HERE = os.path.dirname(os.path.abspath(__file__))


def site_dir():
    try:
        d = bpy.utils.extension_path_user(__package__, path="site", create=True)
    except (ValueError, AttributeError, TypeError):
        d = os.path.join(bpy.utils.user_resource("DATAFILES"), "apocars", "site")
        os.makedirs(d, exist_ok=True)
    return d


class APOCARS_OT_build_library(bpy.types.Operator):
    bl_idname = "apocars.build_library"
    bl_label = "Build vanilla library"
    bl_description = ("Read the game's car parts out of its files for the Test buttons (a minute or two). Needs the vanilla dump: "
                      "in a loaded game, switch on Apocars' Tools > DumpVanilla first")
    _proc = None
    _timer = None
    _step = ""

    def execute(self, context):
        g = game_dir(context)
        data = os.path.join(g, "Apocalypter_Data", "data.unity3d")
        dump = game.dump_dir(g)
        if not os.path.isfile(data):
            self.report({"ERROR"}, "No Apocalypter_Data/data.unity3d in the game folder")
            return {"CANCELLED"}
        if not os.path.isdir(os.path.join(dump, "parts")):
            self.report({"ERROR"}, "No vanilla dump yet: in a loaded game, switch on Apocars' Tools > DumpVanilla, then build again")
            return {"CANCELLED"}
        site = site_dir()
        env = dict(os.environ, PYTHONPATH=site + os.pathsep + os.environ.get("PYTHONPATH", ""))
        need = not os.path.isdir(os.path.join(site, "UnityPy"))
        cmds = []
        if need:
            cmds.append([sys.executable, "-m", "pip", "install", "--upgrade", "--target", site, "UnityPy"])
        cmds.append([sys.executable, os.path.join(HERE, "tools", "library.py"), data, dump, game.library_dir(g)])
        self._cmds, self._env = cmds, env
        self._start_next()
        context.window_manager.modal_handler_add(self)
        self._timer = context.window_manager.event_timer_add(0.5, window=context.window)
        context.window_manager.apocars_status = "Building the vanilla library..."
        return {"RUNNING_MODAL"}

    def _start_next(self):
        cmd = self._cmds.pop(0)
        self._step = "installing UnityPy" if "pip" in cmd else "reading the game's files"
        self._proc = subprocess.Popen(cmd, env=self._env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def modal(self, context, event):
        if event.type != "TIMER":
            return {"PASS_THROUGH"}
        wm = context.window_manager
        if self._proc.poll() is None:
            wm.apocars_status = f"Vanilla library: {self._step}..."
            return {"PASS_THROUGH"}
        out = self._proc.stdout.read() if self._proc.stdout else ""
        if self._proc.returncode != 0:
            wm.event_timer_remove(self._timer)
            wm.apocars_status = f"Vanilla library failed ({self._step}): " + (out.strip().splitlines() or ["?"])[-1][:200]
            self.report({"ERROR"}, wm.apocars_status)
            return {"CANCELLED"}
        if self._cmds:
            self._start_next()
            return {"PASS_THROUGH"}
        wm.event_timer_remove(self._timer)
        game.forget()
        last = (out.strip().splitlines() or ["done"])[-1]
        wm.apocars_status = "Vanilla library: " + last[:200]
        self.report({"INFO"}, wm.apocars_status)
        return {"FINISHED"}


def register():
    bpy.utils.register_class(APOCARS_OT_build_library)
    bpy.types.WindowManager.apocars_status = bpy.props.StringProperty()


def unregister():
    del bpy.types.WindowManager.apocars_status
    bpy.utils.unregister_class(APOCARS_OT_build_library)
