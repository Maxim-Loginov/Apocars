# Colliders from a mesh: the mesh split into convex pieces that hug it (a car's colliders have to be convex, and one
# hull over a whole body would fill in its cabin). CoACD does the split, in the background, installed into the
# add-on's own folder the first time (pip); without it a quicker, rougher split is used.
import os
import subprocess
import sys
import tempfile

import bmesh
import bpy
import numpy as np
from bpy.props import IntProperty, StringProperty
from mathutils import Matrix, Vector

from . import shapes
from .library import site_dir
from .ops import ROLE, S, ensure_root, parent_keep

HERE = os.path.dirname(os.path.abspath(__file__))
HULL_COLOUR = (0.25, 1.0, 0.35, 1.0)
RUNNING = {"name": None}     # the mesh being split (one at a time)


def mesh_data(obj):
    """The mesh as the export sees it (modifiers applied), in the object's own space: vertices, triangles."""
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = ev.to_mesh()
    me.calc_loop_triangles()
    v = np.array([x.co[:] for x in me.vertices], dtype=np.float64)
    f = np.array([t.vertices[:] for t in me.loop_triangles], dtype=np.int32)
    ev.to_mesh_clear()
    return v, f


def rough_split(v, f, count):
    """Without CoACD: the triangles cut in halves along their longest side until there are as many groups as asked,
    each group's convex hull a piece."""
    cent = v[f].mean(axis=1)
    groups = [np.arange(len(f))]
    while len(groups) < count:
        groups.sort(key=lambda g: -len(g))
        g = groups.pop(0)
        if len(g) < 2:
            groups.append(g)
            break
        c = cent[g]
        axis = int(np.argmax(c.max(axis=0) - c.min(axis=0)))
        order = g[np.argsort(c[:, axis])]
        half = len(order) // 2
        groups += [order[:half], order[half:]]
    return [(v[np.unique(f[g].ravel())], None) for g in groups if len(g)]


def hull_object(context, src, name, verts, faces):
    bm = bmesh.new()
    for p in verts:
        bm.verts.new(Vector(p))
    if faces is None or len(faces) == 0:
        bmesh.ops.convex_hull(bm, input=bm.verts[:])
        loose = [x for x in bm.verts if not x.link_faces]
        if loose:
            bmesh.ops.delete(bm, geom=loose, context="VERTS")
    else:
        bm.verts.ensure_lookup_table()
        for tri in faces:
            try:
                bm.faces.new([bm.verts[int(i)] for i in tri])
            except ValueError:
                pass
    if not bm.faces:
        bm.free()
        return None
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    o = bpy.data.objects.new(name, me)
    for coll in src.users_collection:
        coll.objects.link(o)
    o.parent = src
    o.matrix_parent_inverse = Matrix.Identity(4)
    o.display_type = "WIRE"
    o.show_in_front = True
    o.hide_render = True
    o.hide_select = True
    o.color = HULL_COLOUR
    o[ROLE] = "hull"
    return o


def clear_hulls(src):
    for c in list(src.children):
        if c.get(ROLE) == "hull":
            data = c.data
            bpy.data.objects.remove(c, do_unlink=True)
            if data is not None and data.users == 0:
                bpy.data.meshes.remove(data)


def solids_for(s, slot):
    return s.slots[slot].part_solids if 0 <= slot < len(s.slots) else s.solids


def finish(context, src_name, pieces, slot=-1):
    s = S(context)
    solids = solids_for(s, slot)
    src = bpy.data.objects.get(src_name)
    if src is None:
        return 0
    clear_hulls(src)
    shapes.clear(src)
    n = 0
    for i, (v, f) in enumerate(pieces):
        if hull_object(context, src, f"{src.name}_hull_{i + 1:03d}", v, f) is not None:
            n += 1
    sol = next((x for x in solids if x.obj == src), None)
    if sol is None:
        sol = solids.add()
        sol.obj = src
        sol.hidden = src != s.body and slot < 0
    sol.split = n
    sol.mesh = False
    if 0 <= slot < len(s.slots):
        sol.show = s.slots[slot].show_colliders
    shapes.show(sol, sol.show)
    return n


class APOCARS_OT_solid_split(bpy.types.Operator):
    bl_idname = "apocars.solid_split"
    bl_label = "Colliders from mesh"
    bl_description = ("Make colliders from the selected mesh: it's split into convex pieces that hug its shape "
                      "(CoACD, collision-aware convex decomposition), drawn in green. You're asked how many")
    bl_options = {"REGISTER", "UNDO"}
    count: IntProperty(name="Colliders", default=90, min=1, max=255,
                       description="How many convex pieces the mesh is split into. 70-120 is a good range for a car "
                                   "body: fewer is rougher, more is closer and slower. 1: one hull over the whole mesh")
    target: StringProperty(options={"HIDDEN"})
    slot: IntProperty(default=-1, options={"HIDDEN"})   # an attachment made from a mesh: its own colliders
    _proc = None
    _timer = None

    @classmethod
    def poll(cls, context):
        return RUNNING["name"] is None

    @classmethod
    def description(cls, context, props):
        if RUNNING["name"]:
            return f"Busy: '{RUNNING['name']}' is being split. Wait for it to finish"
        return cls.bl_description

    def invoke(self, context, event):
        if RUNNING["name"]:
            self.report({"WARNING"}, f"'{RUNNING['name']}' is still being split")
            return {"CANCELLED"}
        if not self.target:
            o = context.active_object
            if o is None or o.type != "MESH":
                self.report({"WARNING"}, "Select a mesh")
                return {"CANCELLED"}
            self.target = o.name
        return context.window_manager.invoke_props_dialog(self, width=320)

    def draw(self, context):
        col = self.layout.column()
        col.label(text=f"Split '{self.target}' into convex colliders")
        col.prop(self, "count")
        col.label(text="70-120 is a good range for a car body", icon="INFO")
        col.label(text="1: one hull over the whole mesh")

    def execute(self, context):
        s = S(context)
        src = bpy.data.objects.get(self.target)
        if src is None or src.type != "MESH":
            return {"CANCELLED"}
        root = ensure_root(context)
        if src.parent is None and src != root:
            parent_keep(src, root)
        if self.count <= 1:
            # one hull over the whole mesh (blown up a little), as before
            clear_hulls(src)
            solids = solids_for(s, self.slot)
            sol = next((x for x in solids if x.obj == src), None) or solids.add()
            sol.obj, sol.mesh, sol.split = src, True, 0
            sol.hidden = src != s.body and self.slot < 0
            shapes.refresh(sol)
            return {"FINISHED"}
        v, f = mesh_data(src)
        if len(f) == 0:
            self.report({"WARNING"}, "The mesh has no faces")
            return {"CANCELLED"}
        tmp = tempfile.mkdtemp(prefix="apocars_split_")
        self._in, self._out = os.path.join(tmp, "in.npz"), os.path.join(tmp, "out.npz")
        np.savez(self._in, v=v, f=f)
        site = site_dir()
        self._env = dict(os.environ, PYTHONPATH=site + os.pathsep + os.environ.get("PYTHONPATH", ""))
        self._cmds = []
        if not os.path.isdir(os.path.join(site, "coacd")):
            self._cmds.append([sys.executable, "-m", "pip", "install", "--upgrade", "--no-deps", "--target", site, "coacd"])
        self._cmds.append([sys.executable, os.path.join(HERE, "tools", "split.py"), self._in, self._out, str(self.count)])
        self._v, self._f = v, f
        RUNNING["name"] = src.name
        self._start_next()
        wm = context.window_manager
        wm.modal_handler_add(self)
        self._timer = wm.event_timer_add(0.5, window=context.window)
        wm.apocars_status = f"Colliders: splitting '{src.name}' into {self.count}..."
        return {"RUNNING_MODAL"}

    def _start_next(self):
        cmd = self._cmds.pop(0)
        self._step = "installing CoACD" if "pip" in cmd else "splitting (a minute or two)"
        self._proc = subprocess.Popen(cmd, env=self._env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    def modal(self, context, event):
        if event.type != "TIMER":
            return {"PASS_THROUGH"}
        wm = context.window_manager
        if self._proc.poll() is None:
            wm.apocars_status = f"Colliders: {self._step}..."
            return {"PASS_THROUGH"}
        out = self._proc.stdout.read() if self._proc.stdout else ""
        if self._proc.returncode == 0 and self._cmds:
            self._start_next()
            return {"PASS_THROUGH"}
        wm.event_timer_remove(self._timer)
        RUNNING["name"] = None
        pieces = None
        if self._proc.returncode == 0 and os.path.isfile(self._out):
            d = np.load(self._out)
            pieces = [(d[f"v{i}"], d[f"f{i}"]) for i in range(int(d["n"]))]
            how = "CoACD"
        else:
            # no CoACD (no network, no wheel for this system...): the rough split
            pieces = rough_split(self._v, self._f, self.count)
            how = "the quick split (CoACD failed: " + ((out.strip().splitlines() or ["?"])[-1][:120]) + ")"
        n = finish(context, self.target, pieces, self.slot)
        wm.apocars_status = f"Colliders: '{self.target}' is {n} convex pieces ({how})"
        self.report({"INFO"}, wm.apocars_status)
        for area in context.screen.areas if context.screen else []:
            area.tag_redraw()
        return {"FINISHED"}


classes = (APOCARS_OT_solid_split,)


def register():
    for c in classes:
        bpy.utils.register_class(c)


def unregister():
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
