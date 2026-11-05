# The add-on's buttons.
import json
import math
import os

import bpy
from bpy.props import EnumProperty, IntProperty, StringProperty
from mathutils import Matrix, Vector

from . import conv, game, shapes

ROLE = "apocars"          # custom property on our objects: marker, box, point, preview


def S(context):
    return context.scene.apocars


def game_dir(context):
    """The game folder: the one set, else the one found in Steam. Only reads: panels call it while drawing, when
    Blender doesn't let anything be written."""
    s = S(context)
    return bpy.path.abspath(s.game_dir) if s.game_dir else game.find_game()


def ensure_root(context):
    s = S(context)
    if s.root is None:
        root = bpy.data.objects.new(s.asset_id or "asset", None)
        root.empty_display_type = "PLAIN_AXES"
        root.empty_display_size = 0.3
        context.scene.collection.objects.link(root)
        root[ROLE] = "root"
        s.root = root
    return s.root


def parent_keep(obj, parent):
    mw = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_parent_inverse = parent.matrix_world.inverted()
    obj.matrix_world = mw


def body_box(context):
    """The body's box (centre, size) in the root's space (Blender axes); a Poloska's if there is no body."""
    s = S(context)
    root = ensure_root(context)
    if s.body is not None:
        inv = root.matrix_world.inverted()
        pts = [inv @ s.body.matrix_world @ Vector(c) for c in s.body.bound_box]
        lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
        hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
        return (lo + hi) / 2, hi - lo
    c, sz = game.data()["poloska_box"]
    return conv.pos(c), conv.size(sz)


def default_spot(context, key, i):
    """A default place (root space, Blender axes) for the i-th of something: the Poloska's, fitted to the body's box."""
    spots = game.data()["defaults"].get(key) or [[0, 0, 0]]
    spot = spots[i % len(spots)]
    pc, ps = game.data()["poloska_box"]
    pc, ps = conv.pos(pc), conv.size(ps)
    bc, bs = body_box(context)

    def fit(u):
        p = conv.pos(u)
        return Vector(bc[k] + (p[k] - pc[k]) * (bs[k] / ps[k] if ps[k] else 1) for k in range(3))

    if key.startswith("box:"):
        c, size = spot
        sc = conv.size(size)
        return fit(c), Vector(sc[k] * (bs[k] / ps[k] if ps[k] else 1) for k in range(3))
    return fit(spot), None


def new_empty(context, name, kind, location, display="ARROWS", size=0.15, scale=None):
    root = ensure_root(context)
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = display
    e.empty_display_size = size if display != "CUBE" else 1.0
    context.scene.collection.objects.link(e)
    e.parent = root
    e.location = location
    if scale is not None:
        e.scale = scale
    e[ROLE] = kind
    return e


def unique(name):
    n, i = name, 2
    while n in bpy.data.objects:
        n = f"{name}.{i:03d}"
        i += 1
    return n


def remove_tree(obj):
    """Deletes an object and everything under it, with their meshes once nothing else uses them."""
    for c in list(obj.children):
        remove_tree(c)
    data = obj.data if obj.type == "MESH" else None
    bpy.data.objects.remove(obj, do_unlink=True)
    if data is not None and data.users == 0:
        bpy.data.meshes.remove(data)


def delete(context, *objs):
    """What Delete removes: the objects themselves (and what's under them), never the body."""
    body = S(context).body
    for o in objs:
        if o is None or o.name not in bpy.data.objects:
            continue
        if o == body:
            for c in list(o.children):        # whatever we hung on it goes, the body stays
                if c.get(ROLE) in ("marker", "box", "point", "column", "preview", "mount", "collision", "preview_camera", "decor", "hull"):
                    remove_tree(c)
            continue
        remove_tree(o)


def sync_lists(s):
    """Every kind of attachment point and box listed once at least (kinds a newer add-on has, added to an older .blend)."""
    if s.mode != "VEHICLE" or s.root is None:
        return False
    changed = False
    # kinds this add-on no longer has (the fuel tank, for now): their rows go, with the points made for them
    known = {k for k, _ in game.KINDS}
    for i in reversed(range(len(s.slots))):
        x = s.slots[i]
        if x.kind in known:
            continue
        for o in (x.column, x.obj):
            if o is not None and o.name in bpy.data.objects and o.get(ROLE) in ("marker", "column") and o != s.body:
                remove_tree(o)
        s.slots.remove(i)
        changed = True
    have = {x.kind for x in s.slots}
    for kind, _ in game.KINDS:
        if kind not in have:
            for label in game.SLOTS.get(kind, [""]):
                slot = s.slots.add()
                slot.kind, slot.label = kind, label
                if kind == "wheel":
                    slot.steer = label.startswith("front")
                    slot.power = label.startswith("rear")
            changed = True
    have = {b.kind for b in s.boxes}
    for kind, _ in game.BOXES:
        if kind not in have:
            s.boxes.add().kind = kind
            changed = True
    return changed


def add_mount(context, marker, key, local):
    """The vanilla mesh the game puts at this point (engine mount, wheel hub, steering column...), shown under its
    marker as the game has it on its hinge: only to look at, never exported. False if the library hasn't got it."""
    path = game.mounts(game_dir(context)).get(key) if key else None
    if not path or not os.path.isfile(path):
        return False
    before = set(bpy.data.objects)
    prev_active = context.view_layer.objects.active
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    for o in new:
        o[ROLE] = "mount"
        o.hide_render = True
        o.hide_select = True      # clicking it picks the marker's spot, not the mesh
        o.select_set(False)
    for t in [o for o in new if o.parent is None or o.parent not in new]:
        t.parent = marker
        t.matrix_parent_inverse = Matrix.Identity(4)
        t.matrix_basis = local @ t.matrix_basis
    context.view_layer.objects.active = prev_active
    return True


def clear_preview(marker):
    for c in list(marker.children):
        if c.get(ROLE) == "preview":
            remove_tree(c)


# ------------------------------------------------------------------ setup

class APOCARS_OT_setup(bpy.types.Operator):
    bl_idname = "apocars.setup"
    bl_label = "Set up"
    bl_description = "Make the asset's root (everything exported goes under it) and list its attachment points and boxes"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        s = S(context)
        root = ensure_root(context)
        root.name = s.asset_id or root.name
        if s.body is not None and s.body.parent != root:
            parent_keep(s.body, root)
        sync_lists(s)
        return {"FINISHED"}


class APOCARS_OT_use_selected_body(bpy.types.Operator):
    bl_idname = "apocars.use_selected_body"
    bl_label = "Use selected"
    bl_description = "The selected mesh is the body"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        o = context.active_object
        if o is None or o.type != "MESH":
            self.report({"WARNING"}, "Select a mesh")
            return {"CANCELLED"}
        S(context).body = o
        bpy.ops.apocars.setup()
        return {"FINISHED"}


class APOCARS_OT_center_origin(bpy.types.Operator):
    bl_idname = "apocars.center_origin"
    bl_label = "Origin in the middle"
    bl_description = "Move the part's root to the middle of its body, as the game's parts have it (their hinges shift them by half)"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        s = S(context)
        root = ensure_root(context)
        if s.body is None:
            return {"CANCELLED"}
        pts = [s.body.matrix_world @ Vector(c) for c in s.body.bound_box]
        centre = sum(pts, Vector()) / len(pts)
        kids = [(c, c.matrix_world.copy()) for c in root.children]
        root.matrix_world = Matrix.Translation(centre)
        for c, mw in kids:
            c.matrix_world = mw
        return {"FINISHED"}


# ------------------------------------------------------------------ attachment points

def driver_parts(context, b, seat=None, which=("obj", "sit", "exit")):
    """The driver's seat collider (the game's sphere), view (the eyes) and exit point that are missing: where the game's
    car has them, on the driver's seat if there is one (the collider and the view move with it)."""
    d = game.data()
    ctl = d["controls"]
    base = ctl["seat_driver"]["pos"]

    def spot(key):
        if seat is None:
            return default_spot(context, key, 0)[0]
        u = d["defaults"][key][0]
        return conv.pos([u[k] - base[k] for k in range(3)])

    def put(e, on_seat):
        if seat is not None and on_seat:
            e.parent = seat
        return e
    r = ctl.get("entry_radius", 0.2)
    if "obj" in which and b.obj is None:
        b.obj = put(new_empty(context, unique("B_driver_seat"), "box", spot("point:entry"), "SPHERE", 1.0, scale=(r, r, r)), True)
    if "sit" in which and b.sit is None:
        b.sit = put(new_empty(context, unique("P_driver_view"), "point", spot("point:view"), "ARROWS", 0.12), True)
    if "exit" in which and b.exit is None:
        loc = default_spot(context, "point:exit", 0)[0]
        if seat is not None:   # beside the seat, outside the body, on the seat's side
            sx = (seat.matrix_world.translation - ensure_root(context).matrix_world.translation).x
            bc, bs = body_box(context)
            loc = Vector((bc.x + (bs.x / 2 + 0.45) * (1 if sx >= bc.x else -1), loc.y, loc.z))
        b.exit = new_empty(context, unique("P_exit"), "point", loc, "SINGLE_ARROW", 0.2)
    b.owned = True


def panel_boxes(context, panel):
    """The ignition and light switch boxes, on the switch panel's switches as on the game's car (if not there yet)."""
    s = S(context)
    pos = game.data()["controls"]["switch_panel"]["pos"]
    for kind in ("ignition", "headlights"):
        b = next((x for x in s.boxes if x.kind == kind and x.obj is None), None)
        if any(x.kind == kind and x.obj is not None for x in s.boxes):
            continue
        if b is None:
            b = s.boxes.add()
            b.kind = kind
        c, size = game.data()["defaults"]["box:" + kind][0]
        e = new_empty(context, unique(f"B_{kind}"), "box", conv.pos([c[k] - pos[k] for k in range(3)]), "CUBE", scale=conv.size(size) / 2)
        e.parent = panel                   # moves with the panel
        b.obj = e
        b.owned = True


class APOCARS_OT_info(bpy.types.Operator):
    bl_idname = "apocars.info"
    bl_label = ""
    bl_options = {"INTERNAL"}
    what: StringProperty()

    @classmethod
    def description(cls, context, props):
        return game.HELP.get(props.what, "")

    def execute(self, context):
        return {"CANCELLED"}


class APOCARS_OT_slot_add(bpy.types.Operator):
    bl_idname = "apocars.slot_add"
    bl_label = "Add"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty(default=-1)
    kind: StringProperty()

    @classmethod
    def description(cls, context, props):
        s = S(context)
        kind = props.kind or (s.slots[props.index].kind if 0 <= props.index < len(s.slots) else "")
        what = {"steeringwheel": "the game's steering column, with the steering wheel's point on it",
                "switchpanel": "the game's switch panel, with the Ignition and Headlight switch boxes on its switches",
                "fuelcap": "the game's fuel filler cap", "fueltank": "the game's fuel tank"}.get(kind)
        if what is None:
            what = "this attachment point" + (", with the game's mount on it" if kind in ("engine", "radiator", "exhaust", "wheel") else "")
        return f"Add {what}, where the game's car of this size has it: then move it into place"

    def execute(self, context):
        s = S(context)
        if self.index < 0:              # a kind with no row yet
            slot = s.slots.add()
            slot.kind, slot.label = self.kind, ""
            self.index = len(s.slots) - 1
        slot = s.slots[self.index]
        if slot.obj is not None:
            return {"CANCELLED"}
        n = [i for i, x in enumerate(s.slots) if x.kind == slot.kind].index(self.index)
        label = slot.label.replace(" ", "_") or str(n + 1)
        data = game.data()
        shown = True
        if slot.kind == "steeringwheel":
            # the vanilla steering column, and the steering wheel's point on it where the game's car has it
            ctl = data["controls"]
            loc, _ = default_spot(context, "steeringcolumn", n)
            col = new_empty(context, unique(f"A_steering_column_{label}"), "column", loc, "PLAIN_AXES", 0.08)
            shown = add_mount(context, col, "steering_column", conv.rot(ctl["steering_column"]["rot"]).to_4x4())
            d = [ctl["steering_wheel"]["pos"][k] - ctl["steering_column"]["pos"][k] for k in range(3)]
            point = new_empty(context, unique(f"A_steeringwheel_{label}"), "marker", conv.pos(d))
            point.parent = col
            slot.column, slot.obj = col, point
        elif slot.kind in game.CONTROLS:
            key = game.CONTROLS[slot.kind]
            loc, _ = default_spot(context, slot.kind, n)
            slot.obj = new_empty(context, unique(f"A_{slot.kind}_{label}"), "marker", loc, "ARROWS", 0.1)
            shown = add_mount(context, slot.obj, key, conv.rot(data["controls"][key]["rot"]).to_4x4())
            if slot.kind == "switchpanel":
                panel_boxes(context, slot.obj)
        else:
            loc, _ = default_spot(context, slot.kind, n)
            slot.obj = new_empty(context, unique(f"A_{slot.kind}_{label}"), "marker", loc)
            if slot.kind in ("engine", "radiator", "exhaust", "wheel"):
                others = [conv.unity_pos(x.obj.location) for x in s.slots if x.kind == slot.kind and x.obj is not None]
                v = game.variant(slot.kind, conv.unity_pos(loc), others)
                local = conv.rot(v["rot"]).to_4x4()
                if slot.kind == "wheel":
                    # the hub is on the hinge, the suspension's top: half its travel above the wheel's centre
                    travel = s.susp_travel if s.susp_universal else slot.travel
                    local = Matrix.Translation((0, 0, (travel or v.get("spring") or 0.2) * 0.5)) @ local
                shown = add_mount(context, slot.obj, game.mount_key(slot.kind, v["hinge"]), local)
        slot.owned = True
        if slot.kind == "seat" and slot.label == "driver":
            # the driver's seat collider and view come with the driver's seat, on it as on the game's car
            b = next((x for x in s.boxes if x.kind == "driver"), None)
            if b is None:
                b = s.boxes.add()
                b.kind = "driver"
            if b.obj is None or b.sit is None:
                driver_parts(context, b, slot.obj)
        if not shown:
            self.report({"INFO"}, "The vanilla mount isn't in the library: build the vanilla library to see it")
        context.view_layer.objects.active = slot.obj
        return {"FINISHED"}


class APOCARS_OT_slot_delete(bpy.types.Operator):
    bl_idname = "apocars.slot_delete"
    bl_label = "Delete"
    bl_description = ("Delete this attachment point: its object and everything on it (the vanilla mount, test parts). "
                      "An empty extra row goes too. Never deletes the body")
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()

    def execute(self, context):
        s = S(context)
        slot = s.slots[self.index]
        if slot.part_mesh is not None:
            from . import meshparts
            meshparts.release(slot)            # the mesh goes back into the body
        if slot.obj is not None or slot.column is not None:
            delete(context, slot.column, slot.obj)
            slot.obj = slot.column = None
            slot.preview = ""
            slot.owned = False
        elif slot.label.startswith("extra"):
            s.slots.remove(self.index)
        return {"FINISHED"}


class APOCARS_OT_slot_more(bpy.types.Operator):
    bl_idname = "apocars.slot_more"
    bl_label = "Add more"
    bl_description = "Another row for an attachment point of this kind (then + to place it, or pick an object)"
    bl_options = {"REGISTER", "UNDO"}
    kind: StringProperty()

    def execute(self, context):
        s = S(context)
        n = sum(1 for x in s.slots if x.kind == self.kind and x.label.startswith("extra"))
        slot = s.slots.add()
        slot.kind, slot.label = self.kind, f"extra {n + 1}"
        return {"FINISHED"}


def _test_items(self, context):
    s = S(context)
    if self.index >= len(s.slots):
        return [("", "(nothing)", "")]
    parts = game.vanilla_parts(game_dir(context), s.slots[self.index].kind)
    items = [(n, n, f) for n, f in parts]
    _test_items.cache = items or [("", "(no vanilla parts: build the vanilla library)", "")]
    return _test_items.cache


class APOCARS_OT_slot_test(bpy.types.Operator):
    bl_idname = "apocars.slot_test"
    bl_label = "Test"
    bl_description = ("Show one of the game's parts of this kind on it, fitted as the game fits it (doors and lids by "
                      "their hinge edge), to check the point. Only to look at: never exported")
    bl_options = {"REGISTER", "UNDO"}
    bl_property = "part"
    index: IntProperty()
    part: EnumProperty(items=_test_items, name="Vanilla part")

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        s = S(context)
        slot = s.slots[self.index]
        if slot.obj is None:
            bpy.ops.apocars.slot_add(index=self.index)
        files = dict(game.vanilla_parts(game_dir(context), slot.kind))
        path = files.get(self.part)
        if not path or not os.path.isfile(path):
            self.report({"WARNING"}, "Not in the vanilla library: build it (Export step)")
            return {"CANCELLED"}
        place_preview(context, slot, path)
        slot.preview = self.part
        return {"FINISHED"}


class APOCARS_OT_slot_untest(bpy.types.Operator):
    bl_idname = "apocars.slot_untest"
    bl_label = "Clear test"
    bl_description = "Take the test part off"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()

    def execute(self, context):
        slot = S(context).slots[self.index]
        if slot.obj is not None:
            clear_preview(slot.obj)
        slot.preview = ""
        return {"FINISHED"}


def place_preview(context, slot, path):
    """Imports a library part under the marker, placed as Apocars fits it in game: the vanilla hinge's orientation for
    this side, then the shift that hinge gives (doors, hoods, trunk lids: hinge edge on the hinge, as HingeSlot does)."""
    marker = slot.obj
    clear_preview(marker)
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    tops = [o for o in new if o.parent is None or o.parent not in new]
    for o in new:
        o[ROLE] = "preview"
        o.hide_render = True
        o.select_set(False)
        o.hide_select = True      # only to look at: clicking it picks the point it's on
    s = S(context)
    others = [conv.unity_pos(x.obj.location) for x in s.slots if x.kind == slot.kind and x.obj is not None]
    v = game.variant(slot.kind, conv.unity_pos(marker.location), others)
    hinge_rot = conv.rot(v["rot"]).to_4x4()
    offset_u = Vector(v.get("offset") or (0, 0, 0))
    if slot.kind == "wheel":
        offset_u = Vector((0, 0, 0))   # shown at the wheel's centre (the marker); the hinge is the suspension top above it
    if game.data()["kinds"][slot.kind].get("fit"):
        offset_u = slot_fit_offset(slot.kind, new, tops)
    local = hinge_rot @ Matrix.Translation(conv.pos(offset_u))
    for t in tops:
        t.parent = marker
        t.matrix_parent_inverse = Matrix.Identity(4)
        t.matrix_basis = local @ t.matrix_basis
    # the point being tested is what's selected and active, ready to move
    for o in context.selected_objects:
        o.select_set(False)
    marker.select_set(True)
    context.view_layer.objects.active = marker


def slot_fit_offset(kind, objs, tops):
    """HingeSlot's offset (Unity axes): the part's hinge edge on the hinge."""
    k = {"door": ("front", False, 0.0), "hood": ("rear", True, -0.02), "trunk": ("front", True, 0.0)}[kind]
    edge, flush, z = k
    pts = []
    for o in objs:
        if o.type != "MESH":
            continue
        for c in o.bound_box:
            pts.append(o.matrix_world @ Vector(c))   # the part is at the world origin until placed
    if not pts:
        return Vector((0, 0, 0))
    u = [conv.unity_pos(p) for p in pts]
    hi_y = max(p[1] for p in u)
    e = max(p[2] for p in u) if edge == "front" else min(p[2] for p in u)
    if abs(e) < 0.05:
        return Vector((0, 0, 0))
    return Vector((0.0, -hi_y if flush else 0.0, -e + z))


# ------------------------------------------------------------------ boxes

class APOCARS_OT_box_add(bpy.types.Operator):
    bl_idname = "apocars.box_add"
    bl_label = "Add"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty(default=-1)
    kind: StringProperty()

    @classmethod
    def description(cls, context, props):
        s = S(context)
        kind = props.kind or (s.boxes[props.index].kind if 0 <= props.index < len(s.boxes) else "")
        if kind == "com":
            return "Add the centre of mass point, where the Poloska has it for a body this size: then move it into place"
        if kind == "physlock":
            return ("Add a physics lock box where the game's car of this size has one: items dropped inside it are "
                    "locked to the car and ride along. Move and scale it over the bed or cabin")
        return "Add this box where the game's car of this size has it: then move and scale it into place"

    def execute(self, context):
        s = S(context)
        if self.index < 0:              # a kind with no row yet
            s.boxes.add().kind = self.kind
            self.index = len(s.boxes) - 1
        b = s.boxes[self.index]
        if b.obj is not None:
            return {"CANCELLED"}
        n = [i for i, x in enumerate(s.boxes) if x.kind == b.kind].index(self.index)
        b.owned = True
        if b.kind == "com":
            loc, _ = default_spot(context, "point:com", 0)
            b.obj = new_empty(context, unique("P_centre_of_mass"), "point", loc, "SPHERE", 0.12)
            context.view_layer.objects.active = b.obj
            return {"FINISHED"}
        if b.kind == "driver":
            seat = next((x.obj for x in s.slots if x.kind == "seat" and x.obj is not None and x.label == "driver"), None)
            driver_parts(context, b, seat)
            context.view_layer.objects.active = b.obj
            return {"FINISHED"}
        loc, size = default_spot(context, "box:" + b.kind, n)
        name = {"frontpanel": "dashboard", "attack": "ram_damage"}.get(b.kind, b.kind)
        b.obj = new_empty(context, unique(f"B_{name}"), "box", loc, "CUBE", scale=size / 2)
        context.view_layer.objects.active = b.obj
        return {"FINISHED"}


class APOCARS_OT_box_delete(bpy.types.Operator):
    bl_idname = "apocars.box_delete"
    bl_label = "Delete"
    bl_description = "Delete this box (and the driver's sit and exit points). An empty extra row goes too. Never deletes the body"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()

    def execute(self, context):
        s = S(context)
        b = s.boxes[self.index]
        had = b.obj is not None
        if b.sit is not None:
            exit_preview(context)
        delete(context, b.sit, b.exit, b.obj)
        b.owned = False
        if had:
            b.obj = b.sit = b.exit = None
            return {"FINISHED"}
        if b.obj is None and b.kind == "physlock" and sum(1 for x in s.boxes if x.kind == "physlock") > 1:
            s.boxes.remove(self.index)
            return {"FINISHED"}
        b.obj = b.sit = b.exit = None
        return {"FINISHED"}


class APOCARS_OT_box_more(bpy.types.Operator):
    bl_idname = "apocars.box_more"
    bl_label = "Add more"
    bl_description = "Another box of this kind"
    bl_options = {"REGISTER", "UNDO"}
    kind: StringProperty()

    def execute(self, context):
        s = S(context)
        b = s.boxes.add()
        b.kind = self.kind
        return {"FINISHED"}


class APOCARS_OT_driver_point(bpy.types.Operator):
    bl_idname = "apocars.driver_point"
    bl_label = "Driver point"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()
    which: StringProperty()      # sit (the driver's view) or exit
    remove: bpy.props.BoolProperty()

    @classmethod
    def description(cls, context, props):
        what = {"sit": "the driver's view (the eyes)", "exit": "the exit point",
                "obj": "the seat collider (the sphere the player looks at to get in)"}.get(props.which, "")
        return f"Delete {what}" if props.remove else f"Add {what}, where the game's car has it: then move it into place"

    def execute(self, context):
        b = S(context).boxes[self.index]
        if self.remove:
            if self.which == "sit":
                exit_preview(context)
            delete(context, getattr(b, self.which))
            setattr(b, self.which, None)
        else:
            seat = next((x.obj for x in S(context).slots if x.kind == "seat" and x.obj is not None and x.label == "driver"), None)
            driver_parts(context, b, seat, (self.which,))
        return {"FINISHED"}


PREVIEW = "apocars_preview"


def view_areas():
    for wi, w in enumerate(bpy.context.window_manager.windows):
        for ai, a in enumerate(w.screen.areas):
            if a.type == "VIEW_3D":
                yield f"{wi}/{ai}", a.spaces.active


def exit_preview(context):
    """Back to how the viewports were before Preview, the preview camera gone."""
    sc = context.scene
    if PREVIEW not in sc:
        return False
    try:
        state = json.loads(sc[PREVIEW])
    except ValueError:
        state = {}
    views = state.get("views", {})
    for key, space in view_areas():
        v = views.get(key)
        r3d = space.region_3d
        if v is None or r3d is None:
            continue
        r3d.view_perspective = v["persp"]
        r3d.view_location = v["loc"]
        r3d.view_rotation = v["rot"]
        r3d.view_distance = v["dist"]
        space.lens = v.get("lens", space.lens)
    cam = bpy.data.objects.get(state.get("camera_obj", ""))
    old = bpy.data.objects.get(state.get("scene_camera", "")) if state.get("scene_camera") else None
    sc.camera = old
    if cam is not None and cam.get(ROLE) == "preview_camera":
        data = cam.data
        bpy.data.objects.remove(cam, do_unlink=True)
        if data is not None and data.users == 0:
            bpy.data.cameras.remove(data)
    del sc[PREVIEW]
    return True


class APOCARS_OT_view_preview(bpy.types.Operator):
    bl_idname = "apocars.view_preview"
    bl_label = "Preview"
    bl_description = ("Look through the driver's view as the game will (a camera with a 50° field of view, "
                      "facing the car's front). Moving the view point moves it. Exit Preview puts everything back")
    bl_options = {"REGISTER"}
    index: IntProperty()

    def execute(self, context):
        sc = context.scene
        if exit_preview(context):
            return {"FINISHED"}
        view = S(context).boxes[self.index].sit
        if view is None:
            return {"CANCELLED"}
        state = {"scene_camera": sc.camera.name if sc.camera else "", "views": {}}
        for key, space in view_areas():
            r3d = space.region_3d
            if r3d is None:
                continue
            state["views"][key] = {"persp": r3d.view_perspective, "loc": list(r3d.view_location),
                                   "rot": list(r3d.view_rotation), "dist": r3d.view_distance, "lens": space.lens}
        data = bpy.data.cameras.new("Apocars driver's view")
        data.sensor_fit = "VERTICAL"
        data.angle_y = math.radians(50.0)         # the in-car view (vertical, as Unity's)
        data.clip_start = 0.01
        cam = bpy.data.objects.new("Apocars driver's view", data)
        sc.collection.objects.link(cam)
        cam[ROLE] = "preview_camera"
        cam.hide_select = True
        cam.parent = view
        cam.matrix_parent_inverse = Matrix.Identity(4)
        # a camera looks down its -Z: turned to look along the view's -Y (the car's front), its Z up
        cam.matrix_basis = Matrix.Rotation(math.pi, 4, "Z") @ Matrix.Rotation(math.pi / 2, 4, "X")
        # the view point's own scale doesn't stretch the camera
        sx, sy, sz = view.matrix_world.to_scale()
        cam.matrix_basis = Matrix.Diagonal((1 / sx, 1 / sy, 1 / sz, 1)) @ cam.matrix_basis
        state["camera_obj"] = cam.name
        sc[PREVIEW] = json.dumps(state)
        sc.camera = cam
        for _, space in view_areas():
            if space.region_3d is not None:
                space.region_3d.view_perspective = "CAMERA"
        return {"FINISHED"}


class APOCARS_OT_solid_refresh(bpy.types.Operator):
    bl_idname = "apocars.solid_refresh"
    bl_label = "Redraw colliders"
    bl_description = "Draw the solid colliders again (after editing their meshes)"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        shapes.refresh_all(S(context))
        return {"FINISHED"}


class APOCARS_OT_solid_add(bpy.types.Operator):
    bl_idname = "apocars.solid_add"
    bl_label = "Add box"
    bl_description = "A solid collider box: move and scale it over the body"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        s = S(context)
        c, size = body_box(context)
        sol = s.solids.add()
        sol.obj = new_empty(context, unique("C_solid"), "box", c, "CUBE", scale=size / 2)
        sol.hidden = True
        return {"FINISHED"}


class APOCARS_OT_solid_from_mesh(bpy.types.Operator):
    bl_idname = "apocars.solid_from_mesh"
    bl_label = "From selected meshes"
    bl_description = "The selected meshes are solid: by their box, or (Mesh for collision) by the mesh itself, slightly blown up"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        s = S(context)
        root = ensure_root(context)
        n = 0
        for o in context.selected_objects:
            if o.type != "MESH" or o.get(ROLE) == "preview" or any(x.obj == o for x in s.solids):
                continue
            if o.parent is None:
                parent_keep(o, root)
            sol = s.solids.add()
            sol.obj = o
            sol.mesh = True
            sol.hidden = o != s.body and o.get(ROLE) != "body"
            shapes.refresh(sol)
            n += 1
        if n == 0:
            self.report({"WARNING"}, "Select meshes (the body counts)")
        return {"FINISHED"}


class APOCARS_OT_solid_delete(bpy.types.Operator):
    bl_idname = "apocars.solid_delete"
    bl_label = "Delete"
    bl_description = "Delete this collider and its object (the body is only taken off the list, never deleted)"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()

    def execute(self, context):
        s = S(context)
        o = s.solids[self.index].obj
        if o is not None and o.name in bpy.data.objects:
            if o == s.body or o.get(ROLE) == "decor":
                shapes.clear_all(o)       # the mesh stays, its collider drawings and pieces go
            else:
                delete(context, o)
        s.solids.remove(self.index)
        return {"FINISHED"}


class APOCARS_OT_spawn_toggle(bpy.types.Operator):
    bl_idname = "apocars.spawn_toggle"
    bl_label = "Spawn list"
    bl_options = {"REGISTER", "UNDO"}
    table: StringProperty()
    slot: IntProperty(default=-1, options={"HIDDEN"})   # an attachment made from a mesh: its own lists

    @classmethod
    def description(cls, context, props):
        t = game.SPAWN_INFO.get(props.table)
        return (t[3] + ". Click to add it to this list, or take it off") if t else ""

    def execute(self, context):
        s = S(context)
        spawns = s.slots[self.slot].part_spawns if 0 <= self.slot < len(s.slots) else s.spawns
        for i, x in enumerate(spawns):
            if x.table == self.table:
                spawns.remove(i)
                return {"FINISHED"}
        spawns.add().table = self.table
        return {"FINISHED"}


class APOCARS_OT_steer_cycle(bpy.types.Operator):
    bl_idname = "apocars.steer_cycle"
    bl_label = "Steering"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()

    @classmethod
    def description(cls, context, props):
        s = S(context)
        x = s.slots[props.index] if 0 <= props.index < len(s.slots) else None
        now = "off" if x is None or not x.steer else ("reversed: turns the other way to the steering wheel" if x.steer_reverse
                                                       else "on: turns with the steering wheel")
        return f"Steering ({now}). Click: off, then on, then reversed (as rear-wheel steering), then off again"

    def execute(self, context):
        x = S(context).slots[self.index]
        if not x.steer:
            x.steer, x.steer_reverse = True, False
        elif not x.steer_reverse:
            x.steer_reverse = True
        else:
            x.steer, x.steer_reverse = False, False
        return {"FINISHED"}


class APOCARS_OT_select(bpy.types.Operator):
    bl_idname = "apocars.select"
    bl_label = "Select"
    bl_description = "Select this object (and make it active)"
    bl_options = {"REGISTER", "UNDO"}
    name: StringProperty()

    def execute(self, context):
        o = bpy.data.objects.get(self.name)
        if o is None:
            return {"CANCELLED"}
        for x in context.selected_objects:
            x.select_set(False)
        o.select_set(True)
        context.view_layer.objects.active = o
        return {"FINISHED"}


classes = (APOCARS_OT_steer_cycle, APOCARS_OT_info, APOCARS_OT_spawn_toggle, APOCARS_OT_driver_point, APOCARS_OT_view_preview, APOCARS_OT_solid_refresh,
           APOCARS_OT_setup, APOCARS_OT_use_selected_body, APOCARS_OT_center_origin, APOCARS_OT_slot_add, APOCARS_OT_slot_delete,
           APOCARS_OT_slot_more, APOCARS_OT_slot_test, APOCARS_OT_slot_untest, APOCARS_OT_box_add, APOCARS_OT_box_delete,
           APOCARS_OT_box_more, APOCARS_OT_solid_add, APOCARS_OT_solid_from_mesh, APOCARS_OT_solid_delete, APOCARS_OT_select)


def sync_all():
    for sc in bpy.data.scenes:
        try:
            sync_lists(sc.apocars)
        except Exception:
            pass


@bpy.app.handlers.persistent
def _on_load(_):
    sync_all()


def register():
    for c in classes:
        bpy.utils.register_class(c)
    bpy.app.handlers.load_post.append(_on_load)
    bpy.app.timers.register(lambda: (sync_all(), None)[1], first_interval=0.5)   # the file open when the add-on is switched on


def unregister():
    if _on_load in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(_on_load)
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
