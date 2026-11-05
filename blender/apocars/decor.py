# Decorative: the game's own looks on the asset. Game materials put on the selected mesh (they're the game's material
# in game, its colour and texture in Blender), and pieces of the game's cars (suspensions, drivetrains, engine bays)
# imported where their car has them, to keep as part of the body.
import os

import bpy
from bpy.props import EnumProperty, IntProperty, StringProperty
from mathutils import Matrix

from . import conv, game
from .ops import ROLE, S, delete, ensure_root, game_dir, unique


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def game_material(g, name):
    """A Blender material standing for the game's material of this name (made once, then reused)."""
    for m in bpy.data.materials:
        if m.get("vanilla") == name and m.get("apocars_game"):
            return m
    info = game.materials(g).get(name, {})
    m = bpy.data.materials.new(name)
    m["apocars_game"] = True
    try:
        m.use_nodes = True
    except (AttributeError, TypeError):
        pass
    nt = m.node_tree
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None) if nt else None
    col = info.get("color") or [1, 1, 1, 1]
    lin = [srgb_to_linear(c) for c in col[:3]] + [col[3] if len(col) > 3 else 1.0]
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = lin
        tex = info.get("texture_file")
        path = os.path.join(game.library_dir(g), tex) if tex else None
        if path and os.path.isfile(path):
            img = bpy.data.images.load(path, check_existing=True)
            node = nt.nodes.new("ShaderNodeTexImage")
            node.image = img
            node.location = (bsdf.location.x - 600, bsdf.location.y)
            if any(abs(c - 1.0) > 0.01 for c in col[:3]):
                # the game tints the texture with the colour
                mix = nt.nodes.new("ShaderNodeMix")
                mix.data_type = "RGBA"
                mix.blend_type = "MULTIPLY"
                mix.inputs[0].default_value = 1.0
                mix.location = (bsdf.location.x - 250, bsdf.location.y)
                a, b = [i for i in mix.inputs if i.type == "RGBA"][:2]
                out = next(o for o in mix.outputs if o.type == "RGBA")
                nt.links.new(node.outputs["Color"], a)
                b.default_value = lin
                nt.links.new(out, bsdf.inputs["Base Color"])
            else:
                nt.links.new(node.outputs["Color"], bsdf.inputs["Base Color"])
    m.apocars_vanilla = name          # the game's own material in game (glTF extras "vanilla")
    m.diffuse_color = lin
    return m


_mat_items = {"key": None, "items": []}


def _material_items(self, context):
    g = game_dir(context)
    mats = game.materials(g)
    if _mat_items["key"] != (g, len(mats)):
        items = []
        for n in sorted(mats, key=str.lower):
            info = mats[n]
            items.append((n, n, ("textured" if info.get("texture_file") else "colour only") + (f", shader {info['shader']}" if info.get("shader") else "")))
        _mat_items["key"], _mat_items["items"] = (g, len(mats)), items or [("", "(build the vanilla library first)", "")]
    return _mat_items["items"]


class APOCARS_OT_import_material(bpy.types.Operator):
    bl_idname = "apocars.import_material"
    bl_label = "Import game material"
    bl_description = ("Put one of the game's materials on the selected meshes (in Edit Mode: on the selected faces). "
                      "In Blender it shows the game's colour and texture; in game it is the game's own material")
    bl_options = {"REGISTER", "UNDO"}
    bl_property = "material"
    material: EnumProperty(items=_material_items, name="Game material")

    @classmethod
    def poll(cls, context):
        return any(o.type == "MESH" for o in context.selected_objects) or (context.object is not None and context.object.type == "MESH")

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        g = game_dir(context)
        if not self.material:
            self.report({"WARNING"}, "No game materials: build the vanilla library (Export step)")
            return {"CANCELLED"}
        m = game_material(g, self.material)
        objs = [o for o in context.selected_objects if o.type == "MESH"] or [context.object]
        if context.mode == "EDIT_MESH":
            import bmesh
            for o in objs:
                if o.mode != "EDIT":
                    continue
                if m.name not in [s.material.name for s in o.material_slots if s.material]:
                    o.data.materials.append(m)
                idx = [s.material for s in o.material_slots].index(m)
                bm = bmesh.from_edit_mesh(o.data)
                for f in bm.faces:
                    if f.select:
                        f.material_index = idx
                bmesh.update_edit_mesh(o.data)
        else:
            for o in objs:
                if not o.material_slots:
                    o.data.materials.append(m)
                else:
                    o.active_material = m
        self.report({"INFO"}, f"{self.material} on {len(objs)} mesh(es)")
        return {"FINISHED"}


# ------------------------------------------------------------------ pieces of the game's cars

_decor_items = {"key": None, "items": []}


def _decor_enum(self, context):
    g = game_dir(context)
    pieces = game.decor(g)
    if _decor_items["key"] != (g, len(pieces)):
        items = [(p["id"], f"{p['category']}: {p['car']} {p['name']}", f"From the {p['car']}") for p in pieces]
        _decor_items["key"], _decor_items["items"] = (g, len(pieces)), items or [("", "(build the vanilla library first)", "")]
    return _decor_items["items"]


class APOCARS_OT_decor_add(bpy.types.Operator):
    bl_idname = "apocars.decor_add"
    bl_label = "Add new"
    bl_description = ("Import a piece of one of the game's cars (a suspension, drivetrain or engine bay part) where that "
                      "car has it: move it into place. It's part of the body: exported and drawn in game, with the "
                      "game's own materials")
    bl_options = {"REGISTER", "UNDO"}
    bl_property = "piece"
    piece: EnumProperty(items=_decor_enum, name="Game car part")

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        g = game_dir(context)
        p = next((x for x in game.decor(g) if x["id"] == self.piece), None)
        path = os.path.join(game.library_dir(g), p["file"]) if p else ""
        if not p or not os.path.isfile(path):
            self.report({"WARNING"}, "Not in the vanilla library: build it (Export step)")
            return {"CANCELLED"}
        s = S(context)
        root = ensure_root(context)
        holder = bpy.data.objects.new(unique(f"D_{p['car']}_{p['name']}".replace(" ", "_")), None)
        holder.empty_display_type = "PLAIN_AXES"
        holder.empty_display_size = 0.1
        context.scene.collection.objects.link(holder)
        holder.parent = root
        holder[ROLE] = "decor"
        holder.matrix_basis = Matrix.Translation(conv.pos(p["pos"])) @ conv.rot(p["rot"]).to_4x4()
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=path)
        new = [o for o in bpy.data.objects if o not in before]
        for o in new:
            o[ROLE] = "decor"
            o.select_set(False)
        for t in [o for o in new if o.parent is None or o.parent not in new]:
            t.parent = holder
            t.matrix_parent_inverse = Matrix.Identity(4)
        row = s.decor.add()
        row.obj, row.piece = holder, p["id"]
        for o in context.selected_objects:
            o.select_set(False)
        holder.select_set(True)
        context.view_layer.objects.active = holder
        return {"FINISHED"}


class APOCARS_OT_decor_delete(bpy.types.Operator):
    bl_idname = "apocars.decor_delete"
    bl_label = "Delete"
    bl_description = "Delete this imported piece (all of it)"
    bl_options = {"REGISTER", "UNDO"}
    index: IntProperty()

    def execute(self, context):
        s = S(context)
        delete(context, s.decor[self.index].obj)
        s.decor.remove(self.index)
        return {"FINISHED"}


classes = (APOCARS_OT_import_material, APOCARS_OT_decor_add, APOCARS_OT_decor_delete)


def register():
    for c in classes:
        bpy.utils.register_class(c)


def unregister():
    for c in reversed(classes):
        bpy.utils.unregister_class(c)
