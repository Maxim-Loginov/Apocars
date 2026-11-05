#!/usr/bin/env python3
"""Builds a test vehicle for Apocars with Blender (bpy), the way the add-on lays one out, and exports it:
    python3 make_test_car.py <out folder>     ->  <out>/apocars_test.glb and apocars_test.lua

Blender axes: Z up, the car's front towards -Y, its left towards +X (glTF export turns that into Unity's +Z front)."""
import os
import sys

import bpy

out = sys.argv[-1] if len(sys.argv) > 1 else "."
os.makedirs(out, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

root = bpy.data.objects.new("apocars_test", None)
scene.collection.objects.link(root)


def material(name, color):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = color
    bsdf.inputs["Roughness"].default_value = 0.6
    return m


def box_mesh(name, loc, size, mat):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.active_object
    o.name = name
    o.data.name = name
    o.scale = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    o.data.materials.append(mat)
    o.parent = root
    return o


def empty(name, loc, kind="PLAIN_AXES", scale=(1, 1, 1), rot=(0, 0, 0)):
    o = bpy.data.objects.new(name, None)
    o.empty_display_type = kind
    o.location = loc
    o.rotation_euler = rot
    o.scale = scale
    scene.collection.objects.link(o)
    o.parent = root
    return o


paint = material("Paint", (0.55, 0.05, 0.03, 1))
rust = material("vanilla:metal_rusted_20", (0.4, 0.3, 0.2, 1))   # the game's own material
box_mesh("Body", (0, 0, -0.15), (1.5, 3.4, 0.6), paint)
box_mesh("Cabin", (0, 0.25, 0.45), (1.3, 1.6, 0.6), rust)

# attachment points: (name, kind, location, extra)
points = [
    ("A_engine", "engine", (0, -1.1, -0.2)),
    ("A_radiator", "radiator", (0, -1.55, -0.1)),
    ("A_exhaust", "exhaust", (-0.35, 1.25, -0.32)),
    ("A_wheel_FL", "wheel", (0.7, -1.1, -0.5)),
    ("A_wheel_FR", "wheel", (-0.7, -1.1, -0.5)),
    ("A_wheel_RL", "wheel", (0.7, 1.05, -0.5)),
    ("A_wheel_RR", "wheel", (-0.7, 1.05, -0.5)),
    ("A_gearlever", "gearlever", (0, -0.3, -0.1)),
    ("A_steeringwheel", "steeringwheel", (0.33, -0.45, 0.15)),
    ("A_radio", "radio", (0, -0.6, 0.05)),
    ("A_seat_driver", "seat", (0.33, 0.05, -0.25)),
    ("A_seat_passenger", "seat", (-0.33, 0.05, -0.25)),
    ("A_door_L", "door", (0.76, -0.35, 0.0)),
    ("A_door_R", "door", (-0.76, -0.35, 0.0)),
    ("A_hood", "hood", (0, -0.75, 0.16)),
    ("A_trunk", "trunk", (0, 1.3, 0.16)),
    ("A_bumper_front", "bumper", (0, -1.75, -0.35)),
    ("A_bumper_rear", "bumper", (0, 1.75, -0.35)),
    ("A_window", "window", (0, -0.55, 0.45)),
    ("A_roofrack", "roofrack", (0, 0.25, 0.76)),
]
for name, kind, loc in points:
    empty(name, loc)

# boxes: cube empties span -1..1, so scale = half the size
empty("B_ignition", (0.2, -0.5, 0.05), "CUBE", (0.05, 0.05, 0.05))
empty("B_headlights", (0.05, -0.6, 0.12), "CUBE", (0.03, 0.03, 0.03))
empty("B_driver", (1.2, 0.1, 0.0), "CUBE", (0.05, 1.2, 0.5))
empty("P_sit", (0.3, 0.05, 0.12))
empty("P_exit", (1.3, 0.0, -0.2))
empty("B_physlock", (0, 0.25, 0.25), "CUBE", (0.65, 0.9, 0.55))
empty("B_solid_cabin", (0, 0.25, 0.45), "CUBE", (0.65, 0.8, 0.3))

glb = os.path.join(out, "apocars_test.glb")
bpy.ops.export_scene.gltf(filepath=glb, export_format="GLB", export_extras=True, export_yup=True, use_selection=False)

attach = ",\n".join(
    f'    {{ kind = "{k}", node = "{n}"' + (', power = true' if n in ("A_wheel_RL", "A_wheel_RR") else "") +
    (', steer = true' if n in ("A_wheel_FL", "A_wheel_FR") else "") + " }" for n, k, _ in points)
lua = f'''-- Apocars test vehicle (tools/make_test_car.py)
return {{
  kind = "vehicle",
  id = "apocars_test",
  name = "Apocars Test",
  model = "apocars_test.glb",
  mass = 900,
  attach = {{
{attach},
  }},
  boxes = {{
    {{ kind = "ignition", node = "B_ignition" }},
    {{ kind = "headlights", node = "B_headlights" }},
    {{ kind = "driver", node = "B_driver", sit = "P_sit", exit = "P_exit" }},
    {{ kind = "physlock", node = "B_physlock" }},
    {{ kind = "solid", node = "Body", mesh = true, inflate = 0.02 }},
    {{ kind = "solid", node = "B_solid_cabin" }},
  }},
}}
'''
open(os.path.join(out, "apocars_test.lua"), "w").write(lua)
print("wrote", glb)
