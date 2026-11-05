# Export: the asset as Apocars reads it, a .glb (the root and everything under it, test parts left out) and a .lua
# describing it, into the game (Hot export: Apocars rebuilds it and respawns its copies in place), or built into a
# mod of its own that works without Apocars installed (Build).
import os
import re

import bpy
from mathutils import Matrix

from . import game
from .ops import ROLE, S, ensure_root, game_dir


def problems(context):
    """What would keep the asset from working, and what's only worth knowing (errors, warnings)."""
    s = S(context)
    errors, warnings = [], []
    if not re.fullmatch(r"[A-Za-z0-9_]+", s.asset_id or ""):
        errors.append("ID: letters, digits and _ only")
    if s.root is None:
        errors.append("no root: press Set up")
    if s.body is None:
        errors.append("no body mesh")
    if s.mode == "VEHICLE":
        have = {k: [x for x in s.slots if x.kind == k and x.obj is not None] for k, _ in game.KINDS}
        wheels = have["wheel"]
        for k in ("engine", "radiator"):
            if not have[k]:
                errors.append(f"no {game.KIND_NAMES[k].lower()} attachment point")
        if len(wheels) < 2:
            errors.append("fewer than two wheels")
        if wheels and not any(w.power for w in wheels):
            errors.append("no wheel has Power: the engine would drive nothing")
        if wheels and not any(w.steer for w in wheels):
            warnings.append("no wheel has Steering")
        if not have["exhaust"]:
            warnings.append("no exhaust attachment point")
        boxes = {b.kind: b for b in s.boxes if b.obj is not None}
        if "driver" not in boxes:
            errors.append("no driver's seat collider: nobody can get in")
        elif boxes["driver"].sit is None:
            errors.append("driver's seat: no driver's view (the eyes)")
        elif boxes["driver"].exit is None:
            warnings.append("driver's seat: no exit (one is put beside the car)")
        if "ignition" not in boxes:
            errors.append("no ignition box: the engine can't be started")
        if not any(x.obj is not None for x in s.solids):
            errors.append("no solid collider: it would fall through the ground")
        if not have["fuelcap"] and "fuel" not in boxes:
            warnings.append("no fuel cap (one is put at the left rear)")
        if not have["switchpanel"]:
            warnings.append("no switch panel: its switches work by the steering wheel, unseen")
        if "com" not in boxes:
            warnings.append("no centre of mass: the Poloska's, moved to this body")
    else:
        if not s.part_type:
            errors.append("no part type")
        if not any(x.obj is not None for x in s.solids):
            warnings.append("no solid colliders (its box is used)")
    return errors, warnings


def entry_radius(o, root):
    """The seat collider's radius: a sphere empty's (its size times its scale), or half a mesh's widest side."""
    sc = (root.matrix_world.inverted() @ o.matrix_world).to_scale()
    if o.type == "EMPTY":
        return o.empty_display_size * max(abs(sc.x), abs(sc.y), abs(sc.z))
    return max(o.dimensions) / 2


def objects_to_export(context):
    root = ensure_root(context)
    out = []

    def walk(o):
        if o.get(ROLE) in ("preview", "mount", "collision", "preview_camera", "partmesh"):   # only to look at / its own part
            return
        out.append(o)
        for c in o.children:
            walk(c)
    walk(root)
    return out


def lua_value(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        return f"{v:.6g}"
    return '"' + str(v).replace("\\", "\\\\").replace('"', '\\"') + '"'


KEEP_FALSE = {"seethrough"}   # settings whose default in game is true: false has to be written


def entry(d):
    return "{ " + ", ".join(f"{k} = {lua_value(v)}" for k, v in d.items()
                            if v is not None and (v is not False or k in KEEP_FALSE)) + " }"


def spec(context, model):
    s = S(context)
    lines = [f"-- {s.display_name}: written by the Apocars Blender add-on (edit in Blender: this file is rewritten on export)",
             "return {"]
    lines.append(f'  kind = "{"vehicle" if s.mode == "VEHICLE" else "part"}",')
    lines.append(f"  id = {lua_value(s.asset_id)},")
    lines.append(f"  name = {lua_value(s.display_name)},")
    lines.append(f"  model = {lua_value(model)},")
    lines.append(f"  mass = {lua_value(s.mass)},")
    if s.mode == "VEHICLE":
        lines.append(f"  fuel = {lua_value(s.fuel_capacity)},")
        types = {"OPEN": 0, "LOCKED": 1, "LSD": 2}

        def diff(p, extra=None):
            d = {"type": types[getattr(s, p + "_type")], "stiffness": getattr(s, p + "_stiffness"), "slip": getattr(s, p + "_slip"),
                 "power": getattr(s, p + "_power"), "coast": getattr(s, p + "_coast")}
            d.update(extra or {})
            return entry(d)
        # the centre's output A is the front: NWH gives B the bias
        lines.append(f"  diff = {{ axle = {diff('diff_axle')}, center = {diff('diff_center', {'bias': round(1 - s.diff_center_front, 4)})} }},")
    if s.spawn_like.strip():
        lines.append(f"  spawn_like = {lua_value(s.spawn_like.strip())},")
    spawn = [x for x in s.spawns if x.table in game.SPAWN_INFO and game.SPAWN_INFO[x.table][2] == s.mode]
    lines.append("  spawn = {")
    lines += ["    " + entry({"table": x.table, "weight": x.weight}) + "," for x in spawn]
    lines.append("  },")
    boxes = []
    if s.mode == "VEHICLE":
        lines.append("  attach = {")
        for x in s.slots:
            if x.obj is None or x.kind not in game.KIND_NAMES:
                continue
            e = {"kind": x.kind, "node": x.obj.name}
            if x.kind == "wheel":
                e["power"], e["steer"] = x.power, x.steer
                e["reverse"] = x.steer and x.steer_reverse
                src = s if s.susp_universal else x
                e["travel"] = src.susp_travel if s.susp_universal else x.travel
                e["stiffness"] = src.susp_stiffness if s.susp_universal else x.stiffness
                e["absorption"] = src.susp_absorption if s.susp_universal else x.absorption
            if x.kind == "steeringwheel" and x.column is not None:
                e["column"] = x.column.name
            lines.append("    " + entry(e) + ",")
        lines.append("  },")
        for b in s.boxes:
            if b.obj is None:
                continue
            e = {"kind": b.kind, "node": b.obj.name}
            if b.kind == "driver":
                e["view"] = b.sit.name if b.sit else None
                e["exit"] = b.exit.name if b.exit else None
                e["radius"] = round(entry_radius(b.obj, ensure_root(context)), 4)
            boxes.append(e)
    else:
        lines.append(f"  type = {lua_value(s.part_type)},")
        lines.append(f"  template = {lua_value(s.part_template or game.DEFAULT_TEMPLATE.get(s.part_type, ''))},")
    for sol in s.solids:
        if sol.obj is None:
            continue
        if sol.split > 0:
            # split into convex pieces: each is a collider of its own; the mesh itself is body unless hidden
            for h in sorted((c for c in sol.obj.children if c.get(ROLE) == "hull"), key=lambda c: c.name):
                boxes.append({"kind": "solid", "node": h.name, "mesh": True, "inflate": 0.0, "hidden": True})
            if sol.hidden:
                boxes.append({"kind": "collision_source", "node": sol.obj.name})
            continue
        e = {"kind": "solid", "node": sol.obj.name}
        if sol.mesh and sol.obj.type == "MESH":
            e["mesh"], e["inflate"] = True, sol.inflate
        e["hidden"] = sol.hidden and sol.obj.type == "MESH"
        boxes.append(e)
    lines.append("  boxes = {")
    lines += ["    " + entry(e) + "," for e in boxes]
    lines.append("  },")
    lines.append("}")
    return "\n".join(lines) + "\n"


def tag_game_materials(context):
    """Materials named like the game's (a model made from the game's assets has them) are the game's own in game,
    as if set in the material panel. The count tagged."""
    g = game_dir(context)
    n = 0
    for o in objects_to_export(context) + [x.part_mesh for x in S(context).slots if x.part_mesh is not None]:
        if o.type != "MESH":
            continue
        for slot in o.material_slots:
            m = slot.material
            name = game.game_name(m, g)
            if name and m.get("vanilla") != name:
                m.apocars_vanilla = name      # sets m["vanilla"], what Apocars reads (glTF extras)
                n += 1
    return n


def write(context, folder):
    """Writes <folder>/<id>.glb and <id>.lua: the paths."""
    s = S(context)
    os.makedirs(folder, exist_ok=True)
    root = ensure_root(context)
    from . import shapes
    shapes.refresh_all(s)   # the colliders drawn as they're exported
    tag_game_materials(context)
    glb = os.path.join(folder, s.asset_id + ".glb")
    lua = os.path.join(folder, s.asset_id + ".lua")
    # the root is the asset's origin: exported where it is, put back after
    saved = root.matrix_world.copy()
    root.matrix_world = Matrix.Identity(4)
    try:
        objs = objects_to_export(context)
        prev_sel = list(context.selected_objects)
        prev_active = context.view_layer.objects.active
        for o in prev_sel:
            o.select_set(False)
        unselectable = [o for o in objs if o.hide_select]
        hidden = [o for o in objs if o.hide_get()]
        for o in objs:
            o.hide_select = False      # collider pieces can't be clicked, but have to be selected to be exported
            o.hide_set(False)
            o.select_set(True)
        context.view_layer.objects.active = root
        from . import glbpack
        glbpack.export_glb(glb, folder, use_selection=True, export_extras=True, export_yup=True,
                           export_apply=True, export_cameras=False, export_lights=False)
        for o in objs:
            o.select_set(False)
        for o in unselectable:
            o.hide_select = True
        for o in hidden:
            o.hide_set(True)            # hidden in the viewport stays hidden
        for o in prev_sel:
            o.select_set(True)
        context.view_layer.objects.active = prev_active
    finally:
        root.matrix_world = saved
    with open(lua, "w", encoding="utf-8") as f:
        f.write(spec(context, os.path.basename(glb)))
    if s.mode == "VEHICLE":
        from . import meshparts
        meshparts.write_parts(context, folder)   # meshes made into attachments: parts of their own
    from . import glbpack
    glbpack.prune(folder)                         # pictures no file uses any more
    return glb, lua


class APOCARS_OT_export(bpy.types.Operator):
    bl_idname = "apocars.export"
    bl_label = "Hot export"
    bl_description = ("Write the asset into the game (BepInEx/plugins/<Mod folder>/apocars: a .glb and a .lua). With Apocars "
                      "installed and the game running, it's rebuilt within a second and its copies respawn where they are, "
                      "keeping their fitted parts (a car you're sitting in waits until you get out)")
    bl_options = {"REGISTER"}

    def execute(self, context):
        s = S(context)
        errors, warnings = problems(context)
        if errors:
            self.report({"ERROR"}, "Not exported: " + "; ".join(errors))
            return {"CANCELLED"}
        g = game_dir(context)
        if not g:
            self.report({"ERROR"}, "Where's the game? Set the Game folder")
            return {"CANCELLED"}
        folder = os.path.join(game.bepinex(g), "plugins", s.mod_name or "MyApocars", "apocars")
        glb, lua = write(context, folder)
        s.last_export = lua
        msg = f"Exported {s.asset_id} to {folder}"
        if not game.apocars_installed(g):
            msg += " (Apocars isn't installed in this game: Build makes a mod that works without it)"
        if warnings:
            msg += ". Note: " + "; ".join(warnings)
        self.report({"WARNING"} if warnings else {"INFO"}, msg)
        return {"FINISHED"}


class APOCARS_OT_build(bpy.types.Operator):
    bl_idname = "apocars.build"
    bl_label = "Build mod"
    bl_description = ("Build the asset into a BepInEx mod of its own (BepInEx/plugins/<Mod folder>) that works without "
                      "Apocars installed, and a zip of it next to the .blend to share")
    bl_options = {"REGISTER"}
    asset_id: bpy.props.StringProperty(name="ID", description="The vehicle's unique id in game (letters, digits, _): what saves "
                                                              "and spawn commands know it by")
    display_name: bpy.props.StringProperty(name="Name", description="What the game calls it")
    mod_name: bpy.props.StringProperty(name="Folder", description="The mod's folder in BepInEx/plugins, and its name "
                                                                 "(the zip and the plugin are named after it)")

    def invoke(self, context, event):
        s = S(context)
        self.asset_id, self.display_name, self.mod_name = s.asset_id, s.display_name, s.mod_name
        return context.window_manager.invoke_props_dialog(self, width=360, title="Build mod", confirm_text="Build")

    def draw(self, context):
        col = self.layout.column()
        what = "vehicle" if S(context).mode == "VEHICLE" else "attachment"
        col.prop(self, "asset_id", text=f"ID of the {what}")
        col.prop(self, "display_name", text=f"Name of the {what}")
        col.prop(self, "mod_name", text="Folder name")
        if not re.fullmatch(r"[A-Za-z0-9_]+", self.asset_id or ""):
            col.label(text="ID: letters, digits and _ only", icon="ERROR")

    def execute(self, context):
        from . import build
        s = S(context)
        # what was chosen in the dialog (kept: the next build and export use it too)
        if self.asset_id or self.display_name or self.mod_name:
            if not re.fullmatch(r"[A-Za-z0-9_]+", self.asset_id or ""):
                self.report({"ERROR"}, "Not built: the ID can only have letters, digits and _")
                return {"CANCELLED"}
            s.asset_id = self.asset_id
            s.display_name = self.display_name or self.asset_id
            s.mod_name = self.mod_name or "MyApocars"
            if s.root is not None:
                s.root.name = s.asset_id
        errors, warnings = problems(context)
        if errors:
            self.report({"ERROR"}, "Not built: " + "; ".join(errors))
            return {"CANCELLED"}
        g = game_dir(context)
        if not g:
            self.report({"ERROR"}, "Where's the game? Set the Game folder")
            return {"CANCELLED"}
        try:
            out = build.build(context, g)
        except build.BuildError as e:
            self.report({"ERROR"}, str(e))
            return {"CANCELLED"}
        self.report({"INFO"}, f"Built {out}")
        return {"FINISHED"}


classes = (APOCARS_OT_export, APOCARS_OT_build)


def register():
    for c in classes:
        bpy.utils.register_class(c)


def unregister():
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
