#!/usr/bin/env python3
"""The Blender add-on's data about the game, from an Apocars vanilla dump:  python3 addon_data.py <dump folder> <out.json>

For every kind of attachment point: the vanilla hinge Apocars builds it from (the same table as Assembler.Templates),
that hinge's orientation for each side and end, the shift it gives a fitted part (vehPart_Attach), the spring length
of wheel hinges; default spots (the Poloska's, for a body of its size); and which vanilla parts fit each kind."""
import base64
import json
import os
import struct
import sys

TEMPLATES = {   # kind -> (car, [hinges]) : keep in step with Assembler.Templates
    "engine": ("Junker", ["hinge_engine"]), "radiator": ("Poloska", ["hinge_radiator"]), "exhaust": ("Junker", ["hinge_exhaust"]),
    "wheel": ("Poloska", ["hinge_wheel_FL", "hinge_wheel_FR", "hinge_wheel_RL", "hinge_wheel_RR"]),
    "gearlever": ("Poloska", ["hinge_gearlever_Joint"]), "steeringwheel": ("Poloska", ["hinge_steeringwheel"]),
    "radio": ("Poloska", ["hinge_radio"]), "seat": ("Poloska", ["hinge_seat_driver", "hinge_seat_passenger", "hinge_seat_rear"]),
    "door": ("Poloska", ["hinge_door_L", "hinge_door_R"]), "trunk": ("Junker", ["hinge_trunk"]), "hood": ("Poloska", ["hinge_hood"]),
    "bumper": ("Poloska", ["hinge_bumper_front", "hinge_bumper_rear"]), "window": ("Poloska", ["hinge_windshield"]),
    "roofrack": ("Poloska", ["hinge_roofrack"]), "headlight": ("Poloska", ["hinge_headlight"]),
    # the lift kit's slot (hinge_suspension, under hinge_suspension_parent): suspension_lift_kit fits on it
    "suspension": ("Poloska", ["hinge_suspension_parent/hinge_suspension"]),
}
# the part IDs (vehPart_Attach compareTo) each kind takes
PART_IDS = {"window": "windshield"}
# default spots, Unity axes relative to the car's root, for a Poloska-sized body (scaled to the body's box by the add-on)
DEFAULTS = {
    "engine": [(0, -0.2, 1.05)], "radiator": [(0, -0.2, 1.4)], "exhaust": [(0.3, -0.4, -1.3)],
    "wheel": [(-0.564, -0.525, 0.988), (0.567, -0.525, 0.988), (-0.564, -0.525, -0.841), (0.567, -0.525, -0.841)],
    "gearlever": [(0.001, -0.436, 0.425)], "steeringwheel": [(-0.266, 0.085, 0.411)], "radio": [(-0.029, 0.039, 0.614)],
    "seat": [(-0.323, -0.375, 0.104), (0.325, -0.368, 0.1), (-0.004, -0.166, -0.64)],
    "door": [(-0.625, -0.152, 0.659), (0.626, -0.152, 0.659)], "trunk": [(0, 0.05, -0.95)], "hood": [(0, 0.0, 0.8)],
    "bumper": [(0.002, -0.36, 1.501), (0.002, -0.432, -1.47)], "window": [(-0.002, 0.327, 0.535)],
    "roofrack": [(0, 0.604, -0.265)], "headlight": [(-0.433, -0.133, 1.383), (0.433, -0.133, 1.383)],
    "suspension": [(-0.001, -0.528, 1.092)],
    # boxes: centre and size
    "box:ignition": [((-0.137, -0.023, 0.55), (0.08, 0.06, 0.04))], "box:headlights": [((-0.012, -0.019, 0.546), (0.06, 0.06, 0.06))],
    "box:driver": [((-1.49, -0.034, -0.243), (0.08, 1.07, 2.46))], "box:fuel": [((-0.676, -0.144, -0.473), (0.2, 0.2, 0.2))],
    "box:physlock": [((0, -0.016, -0.21), (1.31, 1.13, 1.84)), ((0.007, -0.225, 1.052), (1.16, 0.72, 0.72))],
    "point:sit": [(-0.293, 0.112, -0.053)], "point:exit": [(-1.106, 0.278, 0.129)],
}
# the skeleton car's controls the add-on places (Assembler.PlaceSkeleton): name path under the Poloska's root
CONTROLS = {"steering_column": "parts/steering rod", "steering_wheel": "hinge_steeringwheel", "switch_panel": "switch_panel",
            "fuel_tank": "parts/fueltank", "fuel_cap": "Fuel", "drive_trigger": "DriveTrigger", "sit_pos": "DriveTrigger/sitPos",
            "exit_pos": "DriveTrigger/exitPos", "seat_driver": "hinge_seat_driver", "handbrake": "handbrake",
            "toolbox": "parts/toolset_1", "adjuster": "parts/toolset_2"}
EYE_HEIGHT = 0.25   # sitting, the player's camera is this far above sitPos (the player's InCar FSM; Assembler.EyeHeight)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz]


def qrot(q, v):
    x, y, z, w = q
    p = qmul(qmul(q, [v[0], v[1], v[2], 0.0]), [-x, -y, -z, w])
    return p[:3]


def root_relative(root, path):
    """An object's position and rotation relative to the car's root (Unity axes)."""
    pos, rot, n = [0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0], root
    for name in path.split("/"):
        n = next(c for c in n["children"] if c["name"] == name)
        d = qrot(rot, n["pos"])
        pos = [pos[i] + d[i] for i in range(3)]
        rot = qmul(rot, n["rot"])
    return [round(x, 5) for x in pos], [round(x, 7) for x in rot]


def centre_of_mass(root):
    for c in root["components"]:
        if c["$type"].startswith("NWH.Common.CoM.VariableCenterOfMass"):
            return c["fields"]["centerOfMass"]
    return [0, -0.39, 0.1]
POLOSKA_BOX = ((0, 0.025, 0.015), (1.32, 1.15, 3.05))   # the Poloska's body, centre and size, for scaling the defaults

TYPES = ("Integer Boolean Float String Color ObjectReference LayerMask Enum Vector2 Vector3 Vector4 Rect Array Character "
         "AnimationCurve FsmFloat FsmInt FsmBool FsmString FsmGameObject FsmOwnerDefault FunctionCall FsmAnimationCurve "
         "FsmEvent FsmObject FsmColor Unsupported GameObject FsmVector3").split()


def attach_offset(node):
    """vehPart_Attach's SetPosition of the fitted Item (state Attach), as x, y, z (Unity), or None."""
    for c in node["components"]:
        fsm = (c.get("fields") or {}).get("fsm") or {}
        if fsm.get("name") != "vehPart_Attach":
            continue
        for st in fsm.get("states") or []:
            ad = st.get("actionData") or {}
            names = ad.get("actionNames") or []
            starts = ad.get("actionStartIndex") or []
            for a, n in enumerate(names):
                if not n.endswith(".SetPosition"):
                    continue
                lo = starts[a]
                hi = starts[a + 1] if a + 1 < len(starts) else len(ad["paramName"])
                vals = {}
                owner = None
                for i in range(lo, hi):
                    pn, pt, pos = ad["paramName"][i], TYPES[ad["paramDataType"][i]], ad["paramDataPos"][i]
                    if pt == "FsmFloat":
                        v = ad["fsmFloatParams"][pos]
                        vals[pn] = None if v.get("useVariable") else v.get("value")
                    if pt == "FsmOwnerDefault":
                        owner = ad["fsmOwnerDefaultParams"][pos].get("ownerOption")
                if owner == 0:
                    continue   # moves the hinge itself, not the part
                if all(vals.get(k) is not None for k in "xyz"):
                    return [vals["x"], vals["y"], vals["z"]]
    return None


def spring(node):
    for c in node["components"]:
        if c["$type"].startswith("NWH.WheelController3D.WheelController"):
            return (c["fields"].get("spring") or {}).get("maxLength", 0.2)
    return None


def part_id(d):
    for c in d["root"]["components"]:
        fsm = (c.get("fields") or {}).get("fsm") or {}
        if fsm.get("name") == "ID":
            for v in (fsm.get("variables") or {}).get("stringVariables") or []:
                if v.get("name") == "ID":
                    return v.get("value")
    return None


def main():
    dump, out = sys.argv[1], sys.argv[2]
    cars = {}
    kinds = {}
    for kind, (car, hinges) in TEMPLATES.items():
        if car not in cars:
            cars[car] = json.load(open(os.path.join(dump, "vehicles", car + ".json"), encoding="utf-8"))
        root = cars[car]["root"]
        variants = []
        for h in hinges:
            node = root
            for name in h.split("/"):
                node = next((n for n in node["children"] if n["name"] == name), None) if node else None
            if node is None:
                continue
            pos, rot = root_relative(root, h) if "/" in h else (node["pos"], node["rot"])
            variants.append({"hinge": h.split("/")[-1], "pos": pos, "rot": rot, "offset": attach_offset(node), "spring": spring(node)})
        kinds[kind] = {"car": car, "part_id": PART_IDS.get(kind, kind), "variants": variants, "fit": kind in ("door", "hood", "trunk")}

    poloska = cars["Poloska"]["root"]
    controls = {}
    for key, path in CONTROLS.items():
        pos, rot = root_relative(poloska, path)
        controls[key] = {"pos": pos, "rot": rot}
    controls["com"] = {"pos": centre_of_mass(poloska)}
    defaults = dict(DEFAULTS)
    defaults["steeringcolumn"] = [controls["steering_column"]["pos"]]
    defaults["switchpanel"] = [controls["switch_panel"]["pos"]]
    defaults["fueltank"] = [controls["fuel_tank"]["pos"]]
    defaults["fuelcap"] = [controls["fuel_cap"]["pos"]]
    defaults["handbrake"] = [controls["handbrake"]["pos"]]
    # the front panel: the dashboard's gauge slot, its trigger box (centre, size) relative to the car's root
    dash = next(c for c in poloska["children"] if c["name"] == "Dashboard")
    box = next(c for c in dash["components"] if c["$type"].startswith("UnityEngine.BoxCollider"))["props"]
    dpos, drot = root_relative(poloska, "Dashboard")
    centre = qrot(drot, box["center"])
    att = next(c for c in poloska["children"] if c["name"] == "CarAttack")
    abox = next(c for c in att["components"] if c["$type"].startswith("UnityEngine.BoxCollider"))["props"]
    apos, arot = root_relative(poloska, "CarAttack")
    ac = qrot(arot, abox["center"])
    defaults["box:attack"] = [[[round(apos[i] + ac[i], 5) for i in range(3)], [round(x, 5) for x in abox["size"]]]]
    defaults["box:frontpanel"] = [[[round(dpos[i] + centre[i], 5) for i in range(3)], [round(x, 5) for x in box["size"]]]]
    defaults["toolbox"] = [controls["toolbox"]["pos"]]
    defaults["adjuster"] = [controls["adjuster"]["pos"]]
    # the driver: the drive trigger's sphere (where you get in), the view (the eyes) and where you get out
    sit = controls["sit_pos"]["pos"]
    defaults["point:entry"] = [controls["drive_trigger"]["pos"]]
    defaults["point:view"] = [[sit[0], round(sit[1] + EYE_HEIGHT, 5), sit[2]]]
    defaults["point:exit"] = [controls["exit_pos"]["pos"]]
    controls["entry_radius"] = 0.2
    for c in poloska["children"]:
        if c["name"] == "DriveTrigger":
            for comp in c["components"]:
                if comp["$type"].startswith("UnityEngine.SphereCollider"):
                    controls["entry_radius"] = comp["props"]["radius"]
    defaults["point:com"] = [controls["com"]["pos"]]

    parts = {}
    for f in sorted(os.listdir(os.path.join(dump, "parts"))):
        if not f.endswith(".json"):
            continue
        d = json.load(open(os.path.join(dump, "parts", f), encoding="utf-8"))
        pid = part_id(d)
        if pid:
            parts.setdefault(pid, []).append(d["name"])
    vehicles = sorted(f[:-5] for f in os.listdir(os.path.join(dump, "vehicles")) if f.endswith(".json"))
    # every part's weight off the car, kg: what CheckTag gives it each time it comes off, else its body's
    part_mass = {}
    for f in sorted(os.listdir(os.path.join(dump, "parts"))):
        if not f.endswith(".json"):
            continue
        d = json.load(open(os.path.join(dump, "parts", f), encoding="utf-8"))
        m = next((c["props"].get("mass") for c in d["root"]["components"] if c["$type"].startswith("UnityEngine.Rigidbody")), None)
        for c in d["root"]["components"]:
            fsm = (c.get("fields") or {}).get("fsm") or {}
            if fsm.get("name") != "CheckTag":
                continue
            for st in fsm.get("states") or []:
                for prop in (st.get("actionData") or {}).get("fsmPropertyParams") or []:
                    fp = prop.get("FloatParameter") or {}
                    if prop.get("PropertyName") == "mass" and not fp.get("useVariable"):
                        m = fp.get("value", m)
        if m:
            part_mass[d["name"]] = round(m, 2)
    # the game's wheels: what their Condition FSM gives the car's wheel when fitted (grip, radius, width)
    wheels = {}
    for f in sorted(os.listdir(os.path.join(dump, "parts"))):
        if not f.endswith(".json"):
            continue
        d = json.load(open(os.path.join(dump, "parts", f), encoding="utf-8"))
        for c in d["root"]["components"]:
            fsm = (c.get("fields") or {}).get("fsm") or {}
            if fsm.get("name") != "Condition":
                continue
            vals = {}
            for st in fsm.get("states") or []:
                if st.get("name") == "broken":
                    continue
                for prop in (st.get("actionData") or {}).get("fsmPropertyParams") or []:
                    fp = prop.get("FloatParameter") or {}
                    if prop.get("PropertyName") in ("forwardFriction.grip", "Radius", "Width") and not fp.get("useVariable"):
                        vals[{"forwardFriction.grip": "grip", "Radius": "radius", "Width": "width"}[prop["PropertyName"]]] = round(fp.get("value", 0), 3)
            if "grip" in vals:
                # its name as the game shows it ("Big Off-Road Wheel / Grip: 1.2" -> "Big Off-Road Wheel")
                for c2 in d["root"]["components"]:
                    f2 = (c2.get("fields") or {}).get("fsm") or {}
                    if f2.get("name") != "ItemName":
                        continue
                    for st in f2.get("states") or []:
                        for sp in (st.get("actionData") or {}).get("fsmStringParams") or []:
                            v = sp.get("value") or ""
                            if "/ Grip" in v and not sp.get("useVariable"):
                                vals["title"] = v.split("/ Grip")[0].strip()
                wheels[d["name"]] = vals
    json.dump({"vehicles": vehicles, "kinds": kinds, "defaults": defaults, "controls": controls, "poloska_box": POLOSKA_BOX, "parts_by_id": parts, "wheels": wheels, "part_mass": part_mass}, open(out, "w"), indent=1)
    print(f"{len(kinds)} kinds, {sum(len(v) for v in parts.values())} parts in {len(parts)} IDs -> {out}")


if __name__ == "__main__":
    main()
