# The Apocars tab in the 3D view's sidebar (N): one panel per step (what you make, its body, decoration, attachment
# points, collision boxes, properties, export), and inside them one per kind of point, box or setting. Each folds
# away by its header (Ctrl+click folds all the others); hovering a kind's header tells what it is.
# And the game-material picker in the material properties.
import bpy

from . import export, game
from .ops import S, game_dir

SPACE = dict(bl_space_type="VIEW_3D", bl_region_type="UI", bl_category="Apocars")


def set_up(context):
    s = S(context)
    return s.root is not None


def vehicle(context):
    s = S(context)
    return s.root is not None and s.mode == "VEHICLE"


def done_icon(done):
    return "CHECKMARK" if done else "DOT"


class APOCARS_PT_main(bpy.types.Panel):
    bl_label = "Apocars"
    bl_idname = "APOCARS_PT_main"
    bl_space_type, bl_region_type, bl_category = SPACE["bl_space_type"], SPACE["bl_region_type"], SPACE["bl_category"]

    def draw(self, context):
        status = context.window_manager.apocars_status
        if status:
            self.layout.label(text=status, icon="INFO")


class Step(bpy.types.Panel):
    bl_space_type, bl_region_type, bl_category = SPACE["bl_space_type"], SPACE["bl_region_type"], SPACE["bl_category"]
    bl_parent_id = "APOCARS_PT_main"

    def done(self, context):
        return False

    def draw_header(self, context):
        self.layout.label(text="", icon=done_icon(self.done(context)))


# ------------------------------------------------------------------ 1. what, 2. body

class APOCARS_PT_what(Step):
    bl_label = "What you're making"
    bl_idname = "APOCARS_PT_what"
    bl_order = 0

    def done(self, context):
        return bool(S(context).asset_id)

    def draw(self, context):
        s = S(context)
        b = self.layout
        g = game_dir(context)
        b.row().prop(s, "mode", expand=True)
        b.prop(s, "asset_id")
        b.prop(s, "display_name")
        b.prop(s, "mod_name")
        b.prop(s, "game_dir")
        if not g:
            b.label(text="Game folder not found: set it", icon="ERROR")
        elif not s.game_dir:
            b.label(text=f"Found: {g}", icon="CHECKMARK")
        elif not game.apocars_installed(g):
            b.label(text="Apocars isn't installed in this game (Build still works)", icon="INFO")


class APOCARS_PT_body(Step):
    bl_label = "Body"
    bl_idname = "APOCARS_PT_body"
    bl_order = 1

    def done(self, context):
        s = S(context)
        return s.body is not None and s.root is not None

    def draw(self, context):
        s = S(context)
        b = self.layout
        row = b.row(align=True)
        row.prop(s, "body", text="")
        row.operator("apocars.use_selected_body", text="", icon="EYEDROPPER")
        if s.root is None or (s.mode == "VEHICLE" and not s.slots):
            b.operator("apocars.setup", icon="ADD")
        else:
            b.label(text=f"Root: {s.root.name}", icon="EMPTY_AXIS")
        b.prop(s, "mass")
        if s.mode == "PART":
            b.prop(s, "part_type")
            b.prop(s, "part_template", text="Made like")
            b.operator("apocars.center_origin", icon="PIVOT_BOUNDBOX")


# ------------------------------------------------------------------ 3. decorative

class APOCARS_PT_decor(Step):
    bl_label = "Decorative"
    bl_idname = "APOCARS_PT_decor"
    bl_description = "The game's own looks on your asset: its materials, and pieces of its cars"
    bl_order = 2

    @classmethod
    def poll(cls, context):
        return set_up(context)

    def done(self, context):
        return len(S(context).decor) > 0

    def draw(self, context):
        s = S(context)
        g = game_dir(context)
        b = self.layout
        b.operator("apocars.import_material", icon="MATERIAL")
        col = b.box().column(align=True)
        r = col.row()
        r.label(text="Game car parts", icon="AUTO")
        r.operator("apocars.decor_add", text="Add new", icon="ADD")
        for i, x in enumerate(s.decor):
            r = col.row(align=True)
            if x.obj is not None:
                r.operator("apocars.select", text=x.obj.name, emboss=False, icon="OBJECT_DATA").name = x.obj.name
            else:
                r.label(text="(deleted)")
            r.operator("apocars.decor_delete", text="", icon="TRASH").index = i
        if not s.decor:
            col.label(text="Suspensions, drivetrains, engine bays of the game's cars")
        if not game.decor(g):
            b.label(text="Needs the vanilla library (Export step)", icon="INFO")


# ------------------------------------------------------------------ 4. attachment points (one panel per kind)

class APOCARS_PT_attach(Step):
    bl_label = "Attachment points"
    bl_idname = "APOCARS_PT_attach"
    bl_order = 3

    @classmethod
    def poll(cls, context):
        return vehicle(context)

    def done(self, context):
        return any(x.obj for x in S(context).slots)

    def draw(self, context):
        r = self.layout.row()
        r.scale_y = 1.2
        r.operator_menu_enum("apocars.mesh_to_part", "kind", text="Make mesh into attachment", icon="MOD_BUILD")
        for i, x in enumerate(S(context).slots):
            if x.part_mesh is not None:
                draw_mesh_part(self.layout.box().column(align=True), context, i, x)
        for kind, title in game.KINDS:
            col = self.layout.box().column(align=True)
            r = col.row(align=True)
            r.operator("apocars.info", text=title, emboss=False, icon=done_icon(any(x.obj for x in S(context).slots if x.kind == kind))).what = kind
            r.operator("apocars.slot_more", text="Add more", icon="ADD").kind = kind
            draw_kind(col, context, kind)
        if not game.index(game_dir(context)):
            self.layout.label(text="Test and the vanilla mounts need the vanilla library (Export step)", icon="INFO")


def draw_mesh_part(col, context, i, x):
    from . import meshparts
    from .colliders import RUNNING
    title = dict(game.KINDS).get(x.kind, x.kind)
    r = col.row(align=True)
    r.label(text=f"{title}: {x.part_mesh.name}", icon="MOD_BUILD")
    r.prop(x, "show_colliders", text="", icon="HIDE_OFF" if x.show_colliders else "HIDE_ON")
    r.operator("apocars.select", text="", icon="RESTRICT_SELECT_OFF").name = x.part_mesh.name
    if x.obj is not None:
        r.operator("apocars.select", text="", icon="EMPTY_ARROWS").name = x.obj.name
    # collision
    sub = col.box().column(align=True)
    r = sub.row(align=True)
    r.label(text="Collision", icon="MESH_CUBE")
    op = r.operator("apocars.part_box", text="Add box", icon="ADD")
    op.index, op.which = i, "solid"
    rr = r.row(align=True)
    rr.enabled = RUNNING["name"] is None
    op = rr.operator("apocars.solid_split", text="Split mesh", icon="MOD_EXPLODE")
    op.target, op.slot = x.part_mesh.name, i
    for j, sol in enumerate(x.part_solids):
        r = sub.row(align=True)
        r.prop(sol, "obj", text="")
        if sol.split > 0:
            r.label(text=f"{sol.split} pieces")
        r.prop(sol, "show", text="", icon="HIDE_OFF" if sol.show else "HIDE_ON")
        op = r.operator("apocars.part_box_delete", text="", icon="TRASH")
        op.index, op.which, op.sub = i, "solid", j
    if not x.part_solids:
        sub.label(text="None: its whole box is used", icon="INFO")
    # physics lock
    sub = col.box().column(align=True)
    r = sub.row(align=True)
    r.label(text="Physics lock", icon="LOCKED")
    op = r.operator("apocars.part_box", text="Add", icon="ADD")
    op.index, op.which = i, "lock"
    hint = sub.row()
    hint.enabled = False
    hint.label(text="Locks items dropped inside it to the part", icon="INFO")
    for j, b in enumerate(x.part_locks):
        r = sub.row(align=True)
        r.prop(b, "obj", text="")
        r.prop(b, "show", text="", icon="HIDE_OFF" if b.show else "HIDE_ON")
        op = r.operator("apocars.part_box_delete", text="", icon="TRASH")
        op.index, op.which, op.sub = i, "lock", j
    # where it spawns
    sub = col.box().column(align=True)
    sub.label(text="Spawn", icon="WORLD")
    template = meshparts.template_of(x)
    sub.prop(x, "part_spawn_vanilla", text=f"Like the game's {template}" if template else "Like the game's part")
    like = template if x.part_spawn_vanilla else x.part_spawn_like.strip()
    if not x.part_spawn_vanilla:
        sub.prop(x, "part_spawn_like", icon="DUPLICATE")
    if like:
        lists = game.spawn_like("PART").get(like)
        hint = sub.row()
        hint.enabled = False
        if lists:
            hint.label(text="In: " + ", ".join(sorted({l.split(" (")[0] for l in lists})), icon="INFO")
        else:
            hint.label(text=f"{like} is in no spawn list: world loot only, as it is", icon="INFO")
    sub.separator()
    sub.label(text="And these lists:" if like else "These lists:")
    on = {y.table: y for y in x.part_spawns}
    for tid, label, mode, _ in game.SPAWN_TABLES:
        if mode != "PART":
            continue
        r = sub.row(align=True)
        op = r.operator("apocars.spawn_toggle", text=label, emboss=False,
                        icon="CHECKBOX_HLT" if tid in on else "CHECKBOX_DEHLT")
        op.table, op.slot = tid, i
        if tid in on:
            r.prop(on[tid], "weight", text="")
    r = sub.row()
    r.enabled = False
    r.label(text="World loot: always (the game's item list)", icon="INFO")


def draw_kind(col, context, kind):
    s = S(context)
    rows = [(i, x) for i, x in enumerate(s.slots) if x.kind == kind]
    if not rows:
        r = col.row(align=True)
        r.label(text="(none)")
        op = r.operator("apocars.slot_add", text="", icon="ADD")
        op.index, op.kind = -1, kind
    for i, x in rows:
        r = col.row(align=True)
        r.prop(x, "obj", text=(x.label or "").capitalize())
        if kind == "wheel":
            r.prop(x, "power", text="", icon="AUTO")
            r.operator("apocars.steer_cycle", text="", depress=x.steer,
                       icon="ARROW_LEFTRIGHT" if x.steer and x.steer_reverse else "DRIVER_ROTATIONAL_DIFFERENCE").index = i
        if x.part_mesh is not None:
            r.label(text=x.part_mesh.name, icon="MESH_DATA")
        if x.obj is None:
            r.operator("apocars.slot_add", text="", icon="ADD").index = i
        else:
            if x.column is not None:
                r.operator("apocars.select", text="", icon="EMPTY_ARROWS").name = x.column.name
            r.operator("apocars.select", text="", icon="RESTRICT_SELECT_OFF").name = x.obj.name
            if kind not in game.NO_PARTS:
                t = r.operator("apocars.slot_test", text="Test" if not x.preview else x.preview[:14], icon="HIDE_OFF")
                t.index = i
                if x.preview:
                    r.operator("apocars.slot_untest", text="", icon="X").index = i
        r.operator("apocars.slot_delete", text="", icon="TRASH").index = i
        if kind == "suspension":
            sub = col.column(align=True)
            sub.prop(x, "stock_mesh", text="Stock", icon="HIDE_ON")
            sub.prop(x, "lifted_mesh", text="Lifted", icon="HIDE_OFF")


# ------------------------------------------------------------------ 5. collision boxes

class APOCARS_PT_collide(Step):
    bl_label = "Collision boxes"
    bl_idname = "APOCARS_PT_collide"
    bl_order = 4

    @classmethod
    def poll(cls, context):
        return set_up(context)

    def done(self, context):
        return any(x.obj for x in S(context).solids)

    def draw(self, context):
        s = S(context)
        if s.mode == "VEHICLE":
            for kind, title in game.BOXES:
                col = self.layout.box().column(align=True)
                r = col.row(align=True)
                r.operator("apocars.info", text=title, emboss=False, icon=done_icon(any(x.obj for x in s.boxes if x.kind == kind))).what = kind
                if kind == "physlock":
                    r.operator("apocars.box_more", text="Add more", icon="ADD").kind = kind
                    hint = col.row()
                    hint.enabled = False
                    hint.label(text="Locks items dropped inside it to the car", icon="INFO")
                draw_box_kind(col, context, kind)
        draw_solid(self.layout.box().column(align=True), context)


def draw_box_kind(col, context, kind):
    s = S(context)
    rows = [(i, x) for i, x in enumerate(s.boxes) if x.kind == kind]
    if not rows:
        r = col.row(align=True)
        r.label(text="(none)")
        op = r.operator("apocars.box_add", text="", icon="ADD")
        op.index, op.kind = -1, kind
    for i, x in rows:
        if kind == "driver":
            driver_rows(col, i, x)
            continue
        r = col.row(align=True)
        r.prop(x, "obj", text="")
        if x.obj is None:
            r.operator("apocars.box_add", text="", icon="ADD").index = i
        else:
            r.operator("apocars.select", text="", icon="RESTRICT_SELECT_OFF").name = x.obj.name
        r.operator("apocars.box_delete", text="", icon="TRASH").index = i


def driver_rows(col, i, x):
    previewing = "apocars_preview" in bpy.context.scene
    for which, title in (("obj", "Seat"), ("sit", "Driver's view"), ("exit", "Exit")):
        r = col.row(align=True)
        r.prop(x, which, text=title)
        if getattr(x, which) is None:
            op = r.operator("apocars.driver_point", text="", icon="ADD")
            op.index, op.which, op.remove = i, which, False
        else:
            if which == "sit":
                r.operator("apocars.view_preview", text="Exit Preview" if previewing else "Preview",
                           icon="CANCEL" if previewing else "VIEW_CAMERA", depress=previewing).index = i
            op = r.operator("apocars.driver_point", text="", icon="TRASH")
            op.index, op.which, op.remove = i, which, True


def draw_solid(col, context):
    from .colliders import RUNNING
    s = S(context)
    r = col.row(align=True)
    r.operator("apocars.info", text="Solid", emboss=False, icon=done_icon(any(x.obj for x in s.solids))).what = "solid"
    r.operator("apocars.solid_add", text="Add box", icon="MESH_CUBE")
    if RUNNING["name"]:
        col.label(text=f"Splitting '{RUNNING['name']}'...", icon="SORTTIME")
    for i, x in enumerate(s.solids):
        box = col.box().column(align=True)
        r = box.row(align=True)
        r.prop(x, "obj", text="")
        r.prop(x, "show", text="", icon="HIDE_OFF" if x.show else "HIDE_ON")
        r.operator("apocars.solid_delete", text="", icon="TRASH").index = i
        if x.obj is None or x.obj.type != "MESH":
            continue
        r = box.row(align=True)
        busy = RUNNING["name"] is not None
        if x.split > 0:
            r.label(text=f"{x.split} pieces", icon="MOD_EXPLODE")
        else:
            r.prop(x, "mesh", text="Whole mesh", toggle=True)
            if x.mesh:
                r.prop(x, "inflate", text="")
        sub = r.row(align=True)
        sub.enabled = not busy
        op = sub.operator("apocars.solid_split", text="Split again" if x.split > 0 else "Split", icon="FILE_REFRESH" if x.split > 0 else "MOD_EXPLODE")
        op.target = x.obj.name
        r = box.row(align=True)
        r.prop(x, "hidden", text="Collision only (not drawn in game)", toggle=False)
    col.separator()
    col.prop(s, "solid_pick", text="Pick")


# ------------------------------------------------------------------ 6. properties

class APOCARS_PT_props(Step):
    bl_label = "Properties"
    bl_idname = "APOCARS_PT_props"
    bl_order = 5

    @classmethod
    def poll(cls, context):
        return set_up(context)

    def done(self, context):
        return len(S(context).spawns) > 0

    def draw(self, context):
        s = S(context)
        col = self.layout.box().column(align=True)
        col.label(text="Spawn", icon="WORLD")
        col.prop(s, "spawn_like", icon="DUPLICATE")
        like = s.spawn_like.strip()
        if like:
            lists = game.spawn_like(s.mode).get(like)
            hint = col.row()
            hint.enabled = False
            if lists:
                hint.label(text="In: " + ", ".join(sorted({l.split(" (")[0] for l in lists})), icon="INFO")
            else:
                hint.label(text="Not in any spawn list the game files show", icon="ERROR")
        col.separator()
        col.label(text="Or these lists:" if not like else "And these lists:")
        on = {x.table: x for x in s.spawns}
        for tid, label, mode, _ in game.SPAWN_TABLES:
            if mode != s.mode:
                continue
            r = col.row(align=True)
            r.operator("apocars.spawn_toggle", text=label, emboss=False,
                       icon="CHECKBOX_HLT" if tid in on else "CHECKBOX_DEHLT").table = tid
            if tid in on:
                r.prop(on[tid], "weight", text="")
        if s.mode == "PART":
            r = col.row()
            r.enabled = False
            r.label(text="World loot: always (the game's item list)", icon="INFO")
            return
        if not on:
            col.label(text="Spawns nowhere: only by hand (Apocars' Tools > Spawn)", icon="INFO")

        col = self.layout.box().column(align=True)
        r = col.row()
        r.label(text="Suspension", icon="MOD_PHYSICS")
        r.prop(s, "susp_universal")
        wheels = [x for x in s.slots if x.kind == "wheel"]
        if s.susp_universal:
            col.prop(s, "susp_travel")
            col.prop(s, "susp_stiffness")
            col.prop(s, "susp_absorption")
        elif not wheels:
            col.label(text="No wheels yet")
        else:
            for x in wheels:
                name = (x.label or "wheel").capitalize() + ("" if x.obj is not None else " (not placed)")
                col.label(text=name)
                r = col.row(align=True)
                r.prop(x, "travel", text="Travel")
                r.prop(x, "stiffness", text="Stiff")
                r.prop(x, "absorption", text="Absorb")
        # the lifted suspension: what the wheels get once the game's lift kit is fitted
        col.separator()
        r = col.row()
        r.label(text="Lifted", icon="TRIA_UP_BAR")
        if not any(x.kind == "suspension" and x.obj is not None for x in s.slots):
            r.label(text="slot between the front wheels")
        if s.susp_universal:
            g = col.grid_flow(columns=2, align=True)
            for p in ("travel", "absorption", "height", "wider"):
                g.prop(s, "susp_lift_" + p)
        elif wheels:
            for x in wheels:
                col.label(text=(x.label or "wheel").capitalize() + ", lifted")
                r = col.row(align=True)
                r.prop(x, "lift_travel", text="Travel")
                r.prop(x, "lift_absorption", text="Absorb")
                r.prop(x, "lift_height", text="Lift")
                r.prop(x, "lift_wider", text="Wider")

        col = self.layout.box().column(align=True)
        col.label(text="Differentials", icon="DRIVER")
        powered = [x for x in s.slots if x.kind == "wheel" and x.power and x.obj is not None]
        axles = len({round(x.obj.location.y / 0.3) for x in powered})
        draw_diff(col, s, "diff_axle", "Axle (the two wheels of a powered axle)")
        sub = col.column(align=True)
        sub.separator()
        draw_diff(sub, s, "diff_center", "Centre (between powered axles)")
        sub.prop(s, "diff_center_front")
        if axles < 2:
            sub.label(text="Used once more than one axle has power", icon="INFO")

        col = self.layout.box().column(align=True)
        col.label(text="Fuel", icon="MOD_FLUID")
        col.prop(s, "fuel_capacity")


def draw_diff(col, s, p, title):
    col.label(text=title)
    col.row(align=True).prop(s, p + "_type", expand=True)
    t = getattr(s, p + "_type")
    if t in ("LOCKED", "LSD"):
        col.prop(s, p + "_stiffness")
    if t == "LSD":
        col.prop(s, p + "_slip")
        r = col.row(align=True)
        r.prop(s, p + "_power")
        r.prop(s, p + "_coast")


# ------------------------------------------------------------------ 7. export

class APOCARS_PT_export(Step):
    bl_label = "Export"
    bl_idname = "APOCARS_PT_export"
    bl_order = 6

    @classmethod
    def poll(cls, context):
        return set_up(context)

    def done(self, context):
        errors, _ = export.problems(context)
        return not errors and bool(S(context).last_export)

    def draw(self, context):
        errors, warnings = export.problems(context)
        b = self.layout
        for e in errors:
            b.label(text=e, icon="ERROR")
        for w in warnings:
            b.label(text=w, icon="INFO")
        r = b.row(align=True)
        r.enabled = not errors
        r.operator("apocars.export", icon="EXPORT")
        r.operator("apocars.build", icon="PACKAGE")
        b.separator()
        b.operator("apocars.build_library", icon="FILE_REFRESH")


class APOCARS_PT_material(bpy.types.Panel):
    bl_space_type = "PROPERTIES"
    bl_region_type = "WINDOW"
    bl_context = "material"
    bl_label = "Apocars"

    @classmethod
    def poll(cls, context):
        return context.material is not None

    def draw(self, context):
        m = context.material
        lay = self.layout
        lay.prop(m, "apocars_vanilla", icon="MATERIAL")
        by_name = game.game_name(m, game_dir(context))
        if m.get("apocars_own"):
            lay.label(text="Your own material in game (named like the game's, kept as it is)", icon="INFO")
        elif m.apocars_vanilla:
            lay.label(text="In game this is the game's own material", icon="INFO")
        elif by_name:
            lay.label(text=f"Named like the game's '{by_name}': it's the game's own in game", icon="INFO")
        if by_name or m.get("apocars_own"):
            lay.prop(m, "apocars_own")


def all_classes():
    return [APOCARS_PT_main, APOCARS_PT_what, APOCARS_PT_body, APOCARS_PT_decor, APOCARS_PT_attach, APOCARS_PT_collide,
            APOCARS_PT_props, APOCARS_PT_export, APOCARS_PT_material]


_registered = []


def register():
    _registered.clear()
    for c in all_classes():
        bpy.utils.register_class(c)
        _registered.append(c)


def unregister():
    for c in reversed(_registered):
        bpy.utils.unregister_class(c)
    _registered.clear()
