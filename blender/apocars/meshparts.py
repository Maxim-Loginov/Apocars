# A mesh of the car made into an attachment: taken out of the body, it's exported as a car part of its own (a door, a
# hood, a wheel...) that the game fits, removes and saves as its own parts, and the car gets the attachment point of
# that kind where the mesh is, turned so the game's hinge holds the part exactly there.
import os

import bpy
from bpy.props import EnumProperty, IntProperty, StringProperty
from mathutils import Matrix, Vector

from . import conv, game
from .ops import ROLE, S, ensure_root, new_empty, parent_keep, unique

# the kinds of attachment point that take a car part
PART_KINDS = ["engine", "radiator", "exhaust", "wheel", "gearlever", "steeringwheel", "radio", "seat", "door", "trunk",
              "hood", "bumper", "window", "roofrack", "headlight"]


def _kind_items(self, context):
    names = dict(game.KINDS)
    return [(k, names.get(k, k), f"Make the selected mesh a {names.get(k, k).lower().rstrip('s')} part, fitted on a "
                                  f"new {names.get(k, k).lower().rstrip('s')} attachment point where it is") for k in PART_KINDS]


def box_in_root(obj, root):
    inv = root.matrix_world.inverted()
    pts = [inv @ obj.matrix_world @ Vector(c) for c in obj.bound_box]
    lo = Vector(min(p[k] for p in pts) for k in range(3))
    hi = Vector(max(p[k] for p in pts) for k in range(3))
    return pts, (lo + hi) / 2


def fit_offset(kind, pts, centre):
    """HingeSlot's shift (Unity axes, the hinge's frame) for a part of this box: its hinge edge on the hinge."""
    edge, flush, z = {"door": ("front", False, 0.0), "hood": ("rear", True, -0.02), "trunk": ("front", True, 0.0)}[kind]
    u = [conv.unity_pos(p - centre) for p in pts]
    hi_y = max(p[1] for p in u)
    e = max(p[2] for p in u) if edge == "front" else min(p[2] for p in u)
    return Vector((0.0, -hi_y if flush else 0.0, -e + z))


def template_for(kind, unity_x):
    """The game's part it works like: the default of its type, a right-hand one on the right where there is one."""
    pid = game.part_id(kind)
    name = game.DEFAULT_TEMPLATE.get(pid, "")
    if unity_x > 0.05 and name.endswith("_L"):
        right = name[:-2] + "_R"
        if right in game.data()["parts_by_id"].get(pid, []):
            return right
    return name


class APOCARS_OT_mesh_to_part(bpy.types.Operator):
    bl_idname = "apocars.mesh_to_part"
    bl_label = "Make mesh into attachment"
    bl_description = ("Make the selected mesh an attachment of the kind you pick: it leaves the body and is exported as "
                      "a car part of its own (the game fits, removes and saves it like its own parts), and the car gets "
                      "that kind of attachment point where the mesh is, so the part fits right there")
    bl_options = {"REGISTER", "UNDO"}
    kind: EnumProperty(items=_kind_items, name="Attachment")

    @classmethod
    def poll(cls, context):
        o = context.active_object
        s = S(context)
        return (o is not None and o.type == "MESH" and o != s.body and s.root is not None and s.mode == "VEHICLE"
                and o.get(ROLE) not in ("mount", "preview", "hull", "collision", "partmesh"))

    def execute(self, context):
        s = S(context)
        mesh = context.active_object
        root = ensure_root(context)
        if mesh.parent is None:
            parent_keep(mesh, root)
        kind = self.kind
        pts, centre = box_in_root(mesh, root)
        others = [conv.unity_pos(x.obj.location) for x in s.slots if x.kind == kind and x.obj is not None]
        v = game.variant(kind, conv.unity_pos(centre), others + [conv.unity_pos(centre)])
        hinge = conv.rot(v["rot"])
        if game.data()["kinds"][kind].get("fit"):
            off = fit_offset(kind, pts, centre)
        else:
            off = Vector(v.get("offset") or (0, 0, 0)) if kind != "wheel" else Vector((0, 0, 0))
        # the point turned back from the game's hinge for this side: the part is held in the car's own frame, its
        # middle (the part's origin) where the mesh's is
        # the free row of this kind whose usual spot is nearest (front left, rear right...), else a new one
        from .ops import default_spot
        free = [(i, x) for i, x in enumerate(s.slots) if x.kind == kind and x.obj is None and x.part_mesh is None]
        rows = [i for i, x in enumerate(s.slots) if x.kind == kind]
        slot = min(free, key=lambda ix: (default_spot(context, kind, rows.index(ix[0]))[0] - centre).length)[1] if free else None
        if slot is None:
            n = sum(1 for x in s.slots if x.kind == kind and x.label.startswith("extra"))
            slot = s.slots.add()
            slot.kind, slot.label = kind, f"extra {n + 1}"
        label = slot.label.replace(" ", "_") or "1"
        marker = new_empty(context, unique(f"A_{kind}_{label}"), "marker", centre - conv.pos(off))
        marker.matrix_basis = Matrix.Translation(centre - conv.pos(off)) @ hinge.inverted().to_4x4()
        slot.obj, slot.owned = marker, True
        slot.part_mesh = mesh
        slot.part_template = template_for(kind, conv.unity_pos(centre)[0])
        mesh[ROLE] = "partmesh"
        self.report({"INFO"}, f"{mesh.name} is a {dict(game.KINDS)[kind].lower()} attachment on {marker.name}")
        return {"FINISHED"}


class APOCARS_OT_part_box(bpy.types.Operator):
    bl_idname = "apocars.part_box"
    bl_label = "Add"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()
    which: StringProperty()      # solid or lock

    @classmethod
    def description(cls, context, props):
        if props.which == "lock":
            return ("Add a physics lock box to this attachment: items dropped inside it are locked to the part, so they "
                    "ride along instead of sliding or falling off (as on the game's roof rack). Move and scale it into place")
        return "Add a collision box to this attachment, over the whole part: move and scale it to fit"

    def execute(self, context):
        s = S(context)
        slot = s.slots[self.index]
        mesh = slot.part_mesh
        if mesh is None:
            return {"CANCELLED"}
        root = ensure_root(context)
        pts, centre = box_in_root(mesh, root)
        lo = Vector(min(p[k] for p in pts) for k in range(3))
        hi = Vector(max(p[k] for p in pts) for k in range(3))
        size = hi - lo
        name = unique(f"{'C' if self.which == 'solid' else 'B'}_{mesh.name}_{'solid' if self.which == 'solid' else 'physlock'}")
        e = new_empty(context, name, "box", centre if self.which == "solid" else centre + Vector((0, 0, size.z * 0.25)),
                      "CUBE", scale=Vector((max(size.x, 0.02), max(size.y, 0.02), max(size.z * (1 if self.which == "solid" else 0.5), 0.02))) / 2)
        parent_keep(e, mesh)          # moves with the part
        if self.which == "solid":
            sol = slot.part_solids.add()
            sol.obj = e
            sol.hidden = True
            sol.show = slot.show_colliders
        else:
            b = slot.part_locks.add()
            b.obj = e
            b.show = slot.show_colliders
        context.view_layer.objects.active = e
        return {"FINISHED"}


class APOCARS_OT_part_box_delete(bpy.types.Operator):
    bl_idname = "apocars.part_box_delete"
    bl_label = "Delete"
    bl_description = "Delete this collider of the attachment (and what draws it); the attachment's mesh stays"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()
    which: StringProperty()
    sub: IntProperty()

    def execute(self, context):
        from . import shapes
        from .ops import delete
        s = S(context)
        slot = s.slots[self.index]
        items = slot.part_solids if self.which == "solid" else slot.part_locks
        o = items[self.sub].obj
        if o is not None and o.name in bpy.data.objects:
            if o == slot.part_mesh:
                shapes.clear_all(o)
            else:
                delete(context, o)
        items.remove(self.sub)
        return {"FINISHED"}


def part_boxes(slot, mesh):
    """The part's colliders and physics lock, as its .lua lists them, and the objects to export with it."""
    from . import shapes
    entries, objs = [], []
    for sol in slot.part_solids:
        o = sol.obj
        if o is None or o.name not in bpy.data.objects:
            continue
        shapes.refresh(sol)
        if sol.split > 0 and o.type == "MESH":
            for h in sorted((c for c in o.children if c.get(ROLE) == "hull"), key=lambda c: c.name):
                entries.append({"kind": "solid", "node": h.name, "mesh": True, "inflate": 0.0, "hidden": True})
                objs.append(h)
        elif o.type == "MESH":
            entries.append({"kind": "solid", "node": o.name, "mesh": sol.mesh or None, "inflate": sol.inflate if sol.mesh else None,
                            "hidden": o != mesh})
            objs.append(o)
        else:
            entries.append({"kind": "solid", "node": o.name})
            objs.append(o)
    for b in slot.part_locks:
        if b.obj is not None and b.obj.name in bpy.data.objects:
            entries.append({"kind": "physlock", "node": b.obj.name})
            objs.append(b.obj)
    return entries, [o for o in objs if o != mesh]


def part_id(s, slot):
    n = [x for x in s.slots if x.kind == slot.kind and x.part_mesh is not None].index(slot) + 1
    return f"{s.asset_id}_{slot.kind}_{n}"


def write_parts(context, folder):
    """Each attachment made from a mesh as a part of its own: <folder>/<car id>_<kind>_<n>.glb + .lua."""
    from .export import entry, lua_value
    s = S(context)
    root = ensure_root(context)
    written = []
    for slot in s.slots:
        mesh = slot.part_mesh
        if mesh is None or slot.obj is None or mesh.name not in bpy.data.objects:
            continue
        pid = part_id(s, slot)
        glb = os.path.join(folder, pid + ".glb")
        lua = os.path.join(folder, pid + ".lua")
        _, centre = box_in_root(mesh, root)
        saved = mesh.matrix_world.copy()
        rel = root.matrix_world.inverted() @ saved
        prev_sel, prev_active = list(context.selected_objects), context.view_layer.objects.active
        entries, extra = part_boxes(slot, mesh)
        states = [(o, o.hide_get(), o.hide_select) for o in [mesh] + extra]
        try:
            # the part's origin is the mesh's middle, its axes the car's
            mesh.matrix_world = Matrix.Translation(-centre) @ rel
            for o in prev_sel:
                o.select_set(False)
            for o, _, _ in states:
                o.hide_select = False
                o.hide_set(False)
                o.select_set(True)
            context.view_layer.objects.active = mesh
            from . import glbpack
            glbpack.export_glb(glb, folder, use_selection=True, export_extras=True,
                               export_yup=True, export_apply=True, export_cameras=False, export_lights=False)
        finally:
            mesh.matrix_world = saved
            for o, hid, unsel in states:
                o.select_set(False)
                o.hide_set(hid)
                o.hide_select = unsel
            for o in prev_sel:
                o.select_set(True)
            context.view_layer.objects.active = prev_active
        title = dict(game.KINDS).get(slot.kind, slot.kind)
        lines = [f"-- {s.display_name} {title.lower()}: made from '{mesh.name}' by the Apocars Blender add-on (rewritten on export)",
                 "return {",
                 '  kind = "part",',
                 f"  id = {lua_value(pid)},",
                 f"  name = {lua_value(s.display_name + ' ' + title.rstrip('s').lower())},",
                 f"  model = {lua_value(os.path.basename(glb))},",
                 f"  type = {lua_value(game.part_id(slot.kind))},",
                 f"  template = {lua_value(slot.part_template or game.DEFAULT_TEMPLATE.get(game.part_id(slot.kind), ''))},",
                 f"  for_car = {lua_value(s.asset_id)},",
                 f"  slot = {lua_value(slot.obj.name)},",
                 "  boxes = {"] + ["    " + entry(e) + "," for e in entries] + [
                 "  },",
                 "}"]
        with open(lua, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        written.append(pid)
    return written


def release(slot):
    """The mesh of an attachment back into the body (its attachment point deleted): its colliders and physics lock go."""
    from . import shapes
    from .ops import remove_tree
    mesh = slot.part_mesh
    for o in [x.obj for x in slot.part_solids] + [x.obj for x in slot.part_locks]:
        if o is not None and o != mesh and o.name in bpy.data.objects:
            remove_tree(o)
    if mesh is not None:
        shapes.clear_all(mesh)
    slot.part_solids.clear()
    slot.part_locks.clear()
    if mesh is not None and mesh.get(ROLE) == "partmesh":
        del mesh[ROLE]
    slot.part_mesh = None
    slot.part_template = ""


classes = (APOCARS_OT_mesh_to_part, APOCARS_OT_part_box, APOCARS_OT_part_box_delete)


def register():
    for c in classes:
        bpy.utils.register_class(c)


def unregister():
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
