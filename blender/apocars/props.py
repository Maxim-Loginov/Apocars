# What the add-on keeps in the .blend: the asset being made (Scene.apocars) and its attachment points, boxes, solids.
import bpy
from bpy.props import BoolProperty, CollectionProperty, EnumProperty, FloatProperty, IntProperty, PointerProperty, StringProperty

from . import game, shapes


def _part_types(self, context):
    return [(t, t, "") for t in game.PART_TYPES]


def _templates(self, context, edit_text):
    pid = self.part_type
    names = game.data()["parts_by_id"].get(pid, [])
    return [n for n in names if edit_text.lower() in n.lower()]


def _pickable(self, o):
    return o.get("apocars") not in ("preview", "mount", "root", "collision", "preview_camera")


def _under(o, root):
    while o is not None:
        if o == root:
            return True
        o = o.parent
    return False


def _adopt(context, o, role):
    """An object picked with the eyedropper goes under the asset's root (where it is), so it's exported with it."""
    if o is None:
        return
    root = context.scene.apocars.root
    if root is not None and o != root and not _under(o, root):
        mw = o.matrix_world.copy()
        o.parent = root
        o.matrix_parent_inverse = root.matrix_world.inverted()
        o.matrix_world = mw
    if "apocars" not in o:
        o["apocars"] = role


def _slot_update(self, context):
    _adopt(context, self.obj, "marker")


def _box_update(self, context):
    _adopt(context, self.obj, "box")
    _adopt(context, self.sit, "point")
    _adopt(context, self.exit, "point")


def _solid_pick(self, context):
    o = self.solid_pick
    if o is None:
        return
    if not any(x.obj == o for x in self.solids):
        _adopt(context, o, "box" if o.type != "MESH" else "solid")
        sol = self.solids.add()
        sol.obj = o
        sol.mesh = o.type == "MESH"
        sol.hidden = o.type == "MESH" and o != self.body
        shapes.refresh(sol)
    self.solid_pick = None


def _solid_update(self, context):
    shapes.refresh(self)
    shapes.show(self, self.show)


def _solid_show(self, context):
    shapes.show(self, self.show)


DIFF_TYPES = [("OPEN", "Open", "Splits the torque evenly, whatever the grip: a spinning wheel takes it all from the others "
                               "(the game's axles)"),
              ("LOCKED", "Locked", "Both sides turn together: the drive gets through while one side has grip (the game's "
                                   "four-wheel drive centre)"),
              ("LSD", "Limited slip", "Open, but sends torque away from a side that spins faster, up to the slip torque")]


def diff_props(prefix, default_type, what):
    return {
        f"{prefix}_type": EnumProperty(name="Type", items=DIFF_TYPES, default=default_type, description=f"How the {what} splits the drive"),
        f"{prefix}_stiffness": FloatProperty(name="Stiffness", default=0.5, min=0.0, max=1.0,
                                             description="Locked and limited slip: how hard it holds both sides together (the game's: 0.5)"),
        f"{prefix}_slip": FloatProperty(name="Slip torque", default=400.0, min=0.0, max=20000.0, step=1000,
                                        description="Limited slip: the most torque it moves to the slower side, in Nm (the game's: 400)"),
        f"{prefix}_power": FloatProperty(name="Power ramp", default=1.0, min=0.0, max=1.0,
                                         description="Limited slip: how much it locks under power (the game's: 1)"),
        f"{prefix}_coast": FloatProperty(name="Coast ramp", default=0.5, min=0.0, max=1.0,
                                         description="Limited slip: how much it locks coasting and braking on the engine (the game's: 0.5)"),
    }


def _box_show(self, context):
    if self.obj is not None:
        try:
            self.obj.hide_set(not self.show)
        except RuntimeError:
            pass


def _part_show(self, context):
    """An attachment's colliders and physics lock, all shown or hidden in the viewport."""
    for sol in self.part_solids:
        sol.show = self.show_colliders
    for b in self.part_locks:
        b.show = self.show_colliders


def _suspension_mesh(self, o):
    return o.type in {"MESH", "EMPTY"} and o.get("apocars") is None


def lift_props(prefix, many):
    """The lifted suspension's settings (used once the game's lift kit is fitted): on a wheel, or for all of them."""
    w = "wheels" if many else "wheel"
    return {
        f"{prefix}travel": FloatProperty(
            name="Travel", default=0.3, min=0.02, max=1.5, unit="LENGTH", precision=3,
            description=f"Lifted: how far the {w} move up and down. Longer travel also lifts the car (the game's cars: 0.3 m, from 0.2)"),
        f"{prefix}absorption": FloatProperty(
            name="Absorption", default=1500.0, min=0.0, max=100000.0, step=1000,
            description="Lifted: the dampers' rate (bump and rebound). The game's lifted cars are softer (the Poloska: 1500, from 3000)"),
        f"{prefix}height": FloatProperty(
            name="Lift", default=0.0, min=-0.5, max=1.0, unit="LENGTH", precision=3,
            description=f"Lifted: how much lower the {w} sit on their mounts, raising the car by as much, on top of the longer "
                        "travel (the game's big cars: up to 0.26 m)"),
        f"{prefix}wider": FloatProperty(
            name="Wider", default=0.2, min=-0.5, max=1.0, unit="LENGTH", precision=3,
            description=f"Lifted: how far out the {w} move on each side, for a wider track (the game's cars: 0.2-0.26 m)"),
    }


class ApocarsSlot(bpy.types.PropertyGroup):
    kind: StringProperty()
    label: StringProperty()
    obj: PointerProperty(type=bpy.types.Object, name="Point", poll=_pickable, update=_slot_update,
                         description="The attachment point: Add makes one, or pick any object (its origin is the point)")
    column: PointerProperty(type=bpy.types.Object, name="Column", description="The steering column the steering wheel point is on")
    owned: BoolProperty(description="Made by Add (Delete removes it); a picked object is only let go")
    part_mesh: PointerProperty(type=bpy.types.Object, name="Part",
                               description="The mesh made into this attachment: exported as a car part of its own")
    part_template: StringProperty(description="The game's part it works like (its FSMs, sounds, opening)")
    show_colliders: BoolProperty(name="Show colliders", default=True, update=_part_show,
                                 description="Show this attachment's colliders and physics lock in the viewport. Only in "
                                             "Blender: they work in game either way")
    power: BoolProperty(name="Power", description="This wheel turns with the engine (a differential drives it)")
    steer: BoolProperty(name="Steering", description="This wheel turns with the steering wheel")
    steer_reverse: BoolProperty(name="Reversed", description="It turns the other way to the steering wheel (as rear-wheel steering)")
    preview: StringProperty(description="The vanilla part shown on it (never exported)")
    travel: FloatProperty(name="Travel", default=0.2, min=0.02, max=1.5, unit="LENGTH", precision=3,
                          description="How far the wheel moves up and down: the suspension spring's length (the game's cars: 0.2 m)")
    stiffness: FloatProperty(name="Stiffness", default=5000.0, min=1.0, max=200000.0, step=1000,
                             description="The spring's force at full compression, in newtons: higher holds a heavier car "
                                         "up and gives a harder ride (the game's cars: 5000)")
    absorption: FloatProperty(name="Absorption", default=3000.0, min=0.0, max=100000.0, step=1000,
                              description="The damper's rate (bump and rebound): higher settles bounces sooner, "
                                          "lower lets the car float (the game's cars: 3000)")
    stock_mesh: PointerProperty(type=bpy.types.Object, name="Stock suspension", poll=_suspension_mesh,
                                description="The stock suspension: shown until the game's lift kit is fitted here, hidden "
                                            "while it is. An object of your model (its children go with it)")
    lifted_mesh: PointerProperty(type=bpy.types.Object, name="Lifted suspension", poll=_suspension_mesh,
                                 description="The lifted suspension: hidden until the game's lift kit is fitted here, shown "
                                             "while it is. Model it as it sits lifted (see Properties > Suspension > Lifted)")


class ApocarsBox(bpy.types.PropertyGroup):
    kind: StringProperty()
    obj: PointerProperty(type=bpy.types.Object, name="Box", poll=_pickable, update=_box_update,
                         description="Add makes one, or pick any object (a cube empty or a mesh: its box is used)")
    sit: PointerProperty(type=bpy.types.Object, name="Driver's view", poll=_pickable, update=_box_update,
                         description="The driver's eyes: the in-game camera's place and the way it faces (the car's front) "
                                     "when sitting. Preview looks through it")
    exit: PointerProperty(type=bpy.types.Object, name="Exit", poll=_pickable, update=_box_update,
                          description="Where the player is put when they get out")
    owned: BoolProperty()
    show: BoolProperty(name="Show", default=True, update=_box_show,
                       description="Show this box in the viewport. Only in Blender: it works in game either way")


class ApocarsSolid(bpy.types.PropertyGroup):
    obj: PointerProperty(type=bpy.types.Object, name="Collider")
    mesh: BoolProperty(name="Mesh for collision", update=_solid_update,
                       description="Use this mesh, slightly blown up, as one collider instead of its box. A car's collider "
                                   "can't be hollow: the game uses its convex hull, drawn green over it. Over a whole body it fills "
                                   "the cabin, and the player can't reach the seat or switches through it: Split it instead")
    inflate: FloatProperty(name="Upscale", default=0.02, min=0.0, max=0.5, unit="LENGTH", update=_solid_update,
                           description="How far the collision mesh is blown up past the mesh")
    hidden: BoolProperty(name="Hidden", description="Collision only: not drawn in game")
    show: BoolProperty(name="Show", default=True, update=_solid_show,
                       description="Show this collider's shape (the green wireframes) in the viewport. Only in Blender: "
                                   "it collides in game either way")
    split: IntProperty(name="Pieces", default=0, min=0,
                       description="Split into this many convex pieces (Colliders from mesh); 0: not split")


def _spawn_like_search(self, context, edit_text):
    t = edit_text.lower()
    return [n for n in game.spawn_like(self.mode) if t in n.lower()][:300]


class ApocarsDecor(bpy.types.PropertyGroup):
    obj: PointerProperty(type=bpy.types.Object, name="Piece")
    piece: StringProperty(description="The game car part it was imported from")


class ApocarsSpawn(bpy.types.PropertyGroup):
    table: StringProperty()
    weight: IntProperty(name="Weight", default=1, min=1, max=50,
                        description="How many times it's in the list: each spawn picks one entry of the list at random, "
                                    "so 2 doubles its chance there")


class ApocarsSettings(bpy.types.PropertyGroup):
    mode: EnumProperty(name="Making", items=[("VEHICLE", "Vehicle", "A drivable car, built on the game's own car (its systems, FSMs and IDs)"),
                                             ("PART", "Attachment", "A car part (door, hood, wheel, engine...) that fits the game's cars as theirs do")],
                       description="What you're making")
    asset_id: StringProperty(name="ID", description="Unique id of the asset (letters, digits, _)", default="my_car")
    display_name: StringProperty(name="Name", description="What the game calls it", default="My Car")
    mod_name: StringProperty(name="Mod folder", description="Your mod's folder in BepInEx/plugins (the asset goes in its apocars folder)", default="MyApocars")
    game_dir: StringProperty(name="Game", subtype="DIR_PATH", description="The Apocalypter folder")
    root: PointerProperty(type=bpy.types.Object, name="Root", description="The asset's root empty: everything exported is under it")
    body: PointerProperty(type=bpy.types.Object, name="Body", description="The mesh that is the body", poll=lambda self, o: o.type == "MESH")
    mass: FloatProperty(name="Mass", default=900.0, min=1.0, unit="MASS",
                        description="Its weight in kg (the Poloska: 600). With the centre of mass and suspension, it decides how it drives")
    part_type: EnumProperty(name="Part type", items=_part_types, description="What the part is: decides which hinges it fits")
    part_template: StringProperty(name="Made like", description="The vanilla part it works like (its FSMs, sounds, opening)", search=_templates)
    slots: CollectionProperty(type=ApocarsSlot)
    boxes: CollectionProperty(type=ApocarsBox)
    solids: CollectionProperty(type=ApocarsSolid)
    solid_pick: PointerProperty(type=bpy.types.Object, name="Pick", poll=_pickable, update=_solid_pick,
                                description="Pick an object to make solid (a mesh: by the mesh, or a cube empty: by its box)")
    spawns: CollectionProperty(type=ApocarsSpawn)
    spawn_like: StringProperty(name="Spawn like", search=_spawn_like_search,
                               description="Pick one of the game's vehicles (or items, for an attachment): this one is put "
                                           "in every spawn list that has it, as often as it is there, so it turns up "
                                           "wherever and as often as that one does. Empty: only the lists ticked below")
    decor: CollectionProperty(type=ApocarsDecor)
    susp_universal: BoolProperty(name="Universal", default=True,
                                 description="One suspension setting for every wheel. Off: each wheel has its own")
    susp_travel: FloatProperty(name="Travel", default=0.2, min=0.02, max=1.5, unit="LENGTH", precision=3,
                               description="How far the wheels move up and down: the suspension spring's length (the game's cars: 0.2 m)")
    susp_stiffness: FloatProperty(name="Stiffness", default=5000.0, min=1.0, max=200000.0, step=1000,
                                  description="The springs' force at full compression, in newtons: higher holds a heavier "
                                              "car up and gives a harder ride (the game's cars: 5000)")
    susp_absorption: FloatProperty(name="Absorption", default=3000.0, min=0.0, max=100000.0, step=1000,
                                   description="The dampers' rate (bump and rebound): higher settles bounces sooner, "
                                               "lower lets the car float (the game's cars: 3000)")
    diff_center_front: FloatProperty(name="Front share", default=0.5, min=0.0, max=1.0, subtype="FACTOR",
                                     description="The share of the drive the front axle gets (0.5: even)")
    fuel_capacity: FloatProperty(name="Tank size", default=21.0, min=1.0, max=2000.0, precision=1,
                                 description="How much fuel the car holds, in litres (the Poloska: 21). New copies "
                                             "come as full as the game's do")
    step: IntProperty(default=0)
    last_export: StringProperty()


def _vanilla_search(self, context, edit_text):
    s = context.scene.apocars
    mats = game.materials(bpy.path.abspath(s.game_dir) if s.game_dir else game.find_game())
    t = edit_text.lower()
    return sorted(n for n in mats if t in n.lower())[:300]


def _vanilla_update(self, context):
    """The material's custom property Apocars reads (glTF extras "vanilla"), and its look in the viewport."""
    name = self.apocars_vanilla.strip()
    if name:
        self["vanilla"] = name
        info = game.materials(bpy.path.abspath(context.scene.apocars.game_dir) if context.scene.apocars.game_dir else game.find_game()).get(name)
        if info:
            self.diffuse_color = info.get("color", (1, 1, 1, 1))
    elif "vanilla" in self:
        del self["vanilla"]


for _k, _v in lift_props("lift_", False).items():
    ApocarsSlot.__annotations__[_k] = _v
for _k, _v in lift_props("susp_lift_", True).items():
    ApocarsSettings.__annotations__[_k] = _v
for _k, _v in {**diff_props("diff_axle", "OPEN", "axle differential (between the two wheels of a powered axle)"),
               **diff_props("diff_center", "LOCKED", "centre differential (between powered axles)")}.items():
    ApocarsSettings.__annotations__[_k] = _v

# an attachment made from a mesh has colliders and a physics lock of its own
ApocarsSlot.__annotations__["part_solids"] = CollectionProperty(type=ApocarsSolid)
ApocarsSlot.__annotations__["part_locks"] = CollectionProperty(type=ApocarsBox)

def _set_own(self, v):
    """Kept as it is: no longer the game's material in game."""
    self["apocars_own"] = bool(v)
    if v:
        self.apocars_vanilla = ""
        if "vanilla" in self:
            del self["vanilla"]


classes = (ApocarsBox, ApocarsSolid, ApocarsSlot, ApocarsSpawn, ApocarsDecor, ApocarsSettings)


def register():
    for c in classes:
        bpy.utils.register_class(c)
    bpy.types.Scene.apocars = PointerProperty(type=ApocarsSettings)
    bpy.types.Material.apocars_own = bpy.props.BoolProperty(
        name="Use my own", get=lambda self: bool(self.get("apocars_own", False)), set=_set_own,
        description="Keep this material as it is in Blender, even though it's named like one of the game's")
    bpy.types.Material.apocars_vanilla = StringProperty(
        name="Game material", search=_vanilla_search, update=_vanilla_update,
        description="Use the game's own material of this name in game instead of this one (the library lists them)")


def unregister():
    del bpy.types.Material.apocars_vanilla
    del bpy.types.Material.apocars_own
    del bpy.types.Scene.apocars
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
