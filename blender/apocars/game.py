# The game: where it is, Apocars' data about it (data/game.json), the vanilla library parts are previewed from.
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
_data = None
_index = None
_materials = None
_mounts = None
_decor = None

# the attachment kinds, in the order the panel lists them, with what the user calls them
KINDS = [
    ("engine", "Engine"), ("radiator", "Radiator"), ("exhaust", "Exhaust"), ("wheel", "Wheels"), ("suspension", "Lifted suspension"),
    ("gearlever", "Shift lever"),
    ("steeringwheel", "Steering column"), ("switchpanel", "Switch panel"), ("handbrake", "Handbrake"), ("fuelcap", "Fuel cap"),
    ("toolbox", "Toolbox"), ("adjuster", "Part adjuster"),
    ("radio", "Radio"), ("seat", "Seats"), ("door", "Doors"), ("trunk", "Trunk"),
    ("hood", "Hood"), ("bumper", "Bumpers"), ("window", "Window"), ("roofrack", "Top rack"), ("headlight", "Headlights"),
]
KIND_NAMES = dict(KINDS)
# how many attachment points of a kind a new vehicle starts with (labels), the rest come from "add more"
SLOTS = {
    "wheel": ["front left", "front right", "rear left", "rear right"], "door": ["left", "right"],
    "seat": ["driver", "passenger", "rear"], "bumper": ["front", "rear"], "headlight": ["left", "right"],
}
BOXES = [("ignition", "Ignition"), ("headlights", "Headlight switch"), ("frontpanel", "Dashboard"), ("attack", "Ram damage"), ("driver", "Driver's seat"),
         ("physlock", "Physlock"), ("com", "Centre of mass")]
# kinds that are one of the car's own objects rather than a hinge parts fit on (nothing to Test on them): kind -> its
# entry in data controls and its vanilla mesh in the library's mounts
CONTROLS = {"switchpanel": "switch_panel", "handbrake": "handbrake", "fuelcap": "fuel_cap", "toolbox": "toolbox",
            "adjuster": "adjuster"}
NO_PARTS = set(CONTROLS)
PART_TYPES = ["door", "hood", "trunk", "bumper", "seat", "steeringwheel", "gearlever", "radio", "windshield", "roofrack",
              "exhaust", "wheel", "radiator", "headlight", "attachable", "engine"]
DEFAULT_TEMPLATE = {
    "door": "door_car_1_L", "hood": "hood_1", "trunk": "trunk_1", "bumper": "bumper_1", "seat": "poloska_seat_front",
    "steeringwheel": "steeringwheel_1", "gearlever": "gearlever_1", "radio": "radio_car", "windshield": "windshield_1_glass",
    "roofrack": "roofrack_small", "exhaust": "exhaust_single", "wheel": "wheel_2", "radiator": "radiator_medium",
    "headlight": "Headlight", "attachable": "metal_plate_1", "engine": "1.2L I4 59HP 87Nm Gasoline",
}


def data():
    global _data
    if _data is None:
        with open(os.path.join(HERE, "data", "game.json"), encoding="utf-8") as f:
            _data = json.load(f)
    return _data


# ------------------------------------------------------------------ the game's folder

def steam_libraries():
    roots = [r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam", r"C:\Steam", os.path.expanduser("~/.steam/steam"),
             os.path.expanduser("~/.local/share/Steam")]
    libs = []
    for r in roots:
        vdf = os.path.join(r, "steamapps", "libraryfolders.vdf")
        if os.path.isfile(vdf):
            try:
                text = open(vdf, encoding="utf-8", errors="replace").read()
                libs += [p.replace("\\\\", "\\") for p in re.findall(r'"path"\s+"([^"]+)"', text)]
            except OSError:
                pass
        libs.append(r)
    return libs


_found = None


def find_game():
    """The Apocalypter folder in a Steam library ("" if none). Looked for once per session."""
    global _found
    if _found is None:
        _found = _search_game()
    return _found


def _search_game():
    for lib in steam_libraries():
        d = os.path.join(lib, "steamapps", "common", "Apocalypter")
        if os.path.isfile(os.path.join(d, "Apocalypter.exe")) or os.path.isdir(os.path.join(d, "Apocalypter_Data")):
            return d
    return ""


def bepinex(game):
    return os.path.join(game, "BepInEx") if game else ""


def library_dir(game):
    return os.path.join(bepinex(game), "Apocars-library") if game else ""


def dump_dir(game):
    return os.path.join(bepinex(game), "Apocars-vanilla") if game else ""


def apocars_installed(game):
    return bool(game) and os.path.isfile(os.path.join(bepinex(game), "plugins", "Apocars", "Apocars.dll"))


# ------------------------------------------------------------------ the vanilla library

def index(game):
    global _index
    path = os.path.join(library_dir(game), "index.json")
    if _index is None or _index[0] != path:
        try:
            with open(path, encoding="utf-8") as f:
                _index = (path, json.load(f))
        except (OSError, ValueError):
            _index = (path, {})
    return _index[1]


def materials(game):
    global _materials
    path = os.path.join(library_dir(game), "materials.json")
    if _materials is None or _materials[0] != path:
        try:
            with open(path, encoding="utf-8") as f:
                _materials = (path, json.load(f))
        except (OSError, ValueError):
            _materials = (path, {})
    return _materials[1]


def mounts(game):
    """The vanilla meshes shown on attachment points (library/mounts.json): key -> file."""
    global _mounts
    path = os.path.join(library_dir(game), "mounts.json")
    if _mounts is None or _mounts[0] != path:
        found = {}
        try:
            with open(path, encoding="utf-8") as f:
                found = {k: os.path.join(library_dir(game), v["file"]) for k, v in json.load(f).items()}
        except (OSError, ValueError, KeyError, TypeError):
            pass
        # and any mount file there (a library built by an older add-on lists fewer than its folder may hold)
        folder = os.path.join(library_dir(game), "mounts")
        if os.path.isdir(folder):
            for name in os.listdir(folder):
                if name.endswith(".glb"):
                    found.setdefault(name[:-4], os.path.join(folder, name))
        _mounts = (path, found)
    return _mounts[1]


def decor(game):
    """The game's car pieces the Decorative step imports (library/decor.json): id, car, category, name, file, pos, rot."""
    global _decor
    path = os.path.join(library_dir(game), "decor.json")
    if _decor is None or _decor[0] != path:
        try:
            with open(path, encoding="utf-8") as f:
                _decor = (path, json.load(f))
        except (OSError, ValueError):
            _decor = (path, [])
    return _decor[1] + _game_wheels(game)


def _game_wheels(game):
    """The game's wheels (from the parts library) as decorative pieces: placed where a front left wheel is."""
    idx = index(game)
    wheels = data().get("wheels", {})
    spot = (data()["defaults"].get("wheel") or [[-0.564, -0.525, 0.988]])[0]
    v = (data()["kinds"].get("wheel", {}).get("variants") or [{}])[0]
    out = []
    for n in data()["parts_by_id"].get("wheel", []):
        if n not in idx:
            continue
        w = wheels.get(n, {})
        shown = w.get("title") or idx[n].get("name") or n
        out.append({"id": "wheel/" + n, "car": "", "category": "Wheel", "name": shown, "file": idx[n]["file"],
                    "pos": list(spot), "rot": v.get("rot", [0, 0, 0, 1]),
                    "label": f"Wheel: {shown}" + (f" (grip {w['grip']:g})" if "grip" in w else ""),
                    "note": f"The game's {n}" + (f": radius {w['radius']:g} m, grip {w['grip']:g}" if w else "")})
    return out


def game_name(material, game):
    """The game material a Blender material stands for: the one it's set to, else the one it's named like
    ("seat_8", "seat_8.001"), unless it's marked as its own. None if neither."""
    if material is None or material.get("apocars_own"):
        return None
    if material.get("vanilla"):
        return material["vanilla"]
    name = material.name
    base = re.sub(r"\.\d{3}$", "", name)
    mats = materials(game)
    return base if base in mats else None


_spawn_like = None


def spawn_like(mode):
    """The game's prefabs that are in spawn lists (data/spawn_like.json): name -> the lists, for vehicles or items."""
    global _spawn_like
    if _spawn_like is None:
        try:
            with open(os.path.join(HERE, "data", "spawn_like.json"), encoding="utf-8") as f:
                _spawn_like = json.load(f)
        except (OSError, ValueError):
            _spawn_like = {}
    return _spawn_like.get(mode, {})


def mount_key(kind, variant_hinge=""):
    """Which vanilla mesh an attachment point of a kind shows (the one on the game's hinge it's built from)."""
    if kind in ("engine", "radiator", "exhaust"):
        return kind
    if kind == "wheel":
        return "wheel_" + variant_hinge.rsplit("_", 1)[-1]
    if kind == "steeringwheel":
        return "steering_column"
    return CONTROLS.get(kind)


def forget():
    global _index, _materials, _mounts, _decor
    _index = _materials = _mounts = _decor = None


def vanilla_part_values(template):
    """A game part's own weight off the car (kg) and, for a wheel, its grip: (mass or None, grip or None)."""
    d = data()
    return d.get("part_mass", {}).get(template), d.get("wheels", {}).get(template, {}).get("grip")


def part_id(kind):
    return data()["kinds"].get(kind, {}).get("part_id", kind)


def vanilla_parts(game, kind):
    """(name, file) of every vanilla part that fits a kind of attachment point, as the library has them."""
    pid = part_id(kind)
    idx = index(game)
    names = data()["parts_by_id"].get(pid, [])
    return [(n, os.path.join(library_dir(game), idx[n]["file"])) for n in names if n in idx]


def variant(kind, unity_pos, others):
    """The vanilla hinge an attachment point is made from (as Apocars picks it): by side and end of the car."""
    vs = data()["kinds"][kind]["variants"]
    left = unity_pos[0] < 0
    if kind == "wheel":
        zs = [p[2] for p in others] or [0]
        mid = sum(zs) / len(zs) if len(zs) > 1 else 0
        front = unity_pos[2] >= mid
        return vs[(0 if front else 2) + (0 if left else 1)]
    if kind == "door":
        return vs[0 if left else 1]
    if kind == "bumper":
        return vs[0 if unity_pos[2] >= 0 else 1]
    if kind == "seat":
        front_most = max([p[2] for p in others] or [unity_pos[2]])
        if unity_pos[2] < front_most - 0.4:
            return vs[2]
        return vs[0 if left else 1]
    return vs[0]


# ------------------------------------------------------------------ the game's spawn lists (Apocars' Spawning.Tables)

# id, what the panel calls it, VEHICLE or PART, what it is
SPAWN_TABLES = [
    ("car_camps", "Camps and buildings", "VEHICLE",
     "The car spots of camps and buildings (CarSpawn): Junker, Outrider, PigPen, Poloska, Vulture, PipeRat, Crossbreed, "
     "TinyTyrant, Halfbreed. 7 spots across the map"),
    ("car_all", "Big camps and buildings", "VEHICLE",
     "The car spots of the bigger camps and buildings (CarSpawn_All): the same cars plus the Rust trucks (Rustcargo, "
     "Rustchief, Rustliner). 10 spots"),
    ("car_starter", "Starter base", "VEHICLE",
     "The car at the starter base, where a new game begins (CarSpawn_StarterBase): Poloska, Junker, PipeRat, Crossbreed, "
     "TinyTyrant, Halfbreed"),
    ("car_unique", "Unique car spots", "VEHICLE",
     "Spots that always hold one particular car (CarSpawn_Unique: the Duke, the Rustallion, a Poloska). Added here, "
     "this vehicle shares those spots with them at random"),
    ("trailer", "Trailer spots", "VEHICLE", "Trailer spots (TrailerSpawn): Trailer_Small, Trailer_Big. 4 spots"),
    ("trailer_all", "Trailer and wagon spots", "VEHICLE",
     "Trailer spots of the bigger camps and buildings (TrailerSpawn_All): the trailers and the Scrapwagon. 7 spots"),
    ("item_starter", "Starter base items", "PART",
     "The loose items at the starter base (ItemSpawn_StarterBase): food, gauges, a Poloska exhaust, small wheels, a radio..."),
    ("engine_starter", "Starter base engine", "PART",
     "The engine waiting at the starter base (EngineSpawn_StarterBase): the 594cc I2 or the 1.2L I4"),
    ("item_unique", "Unique item spots", "PART",
     "Spots in caves, camps and buildings that always hold one particular engine (ItemSpawn_Unique). Added here, this "
     "item shares those spots with them at random"),
    ("item_boss", "Boss room loot", "PART",
     "The boss room's loot (ItemSpawn_Unique_BossRoom): armour plates, blast lances, sanity pills..."),
    ("trader_merchant", "Merchants", "PART",
     "What merchants sell and buy (ItemBuySellList): 116 items, from bandages and tools to engines and turbos"),
    ("trader_mechanic", "Mechanic", "PART",
     "The mechanic's stock and what he buys (MechanicItemsSpawn_V1, Sell_Items_mechanic): engines, radiators, turbos, "
     "superchargers, the lift kit"),
    ("trader_medic", "Organic mechanic", "PART",
     "The organic mechanic's stock (OrganicMechanicItemsSpawn_V1, Sell_Items_organic_mechanic): bandages, first aid, "
     "cigars, pills"),
]
SPAWN_INFO = {t[0]: t for t in SPAWN_TABLES}
ITEM_LIST_NOTE = ("Every Apocars part is in the game's item list (ItemList): the game saves items by it, its random "
                  "loot spots (ItemSpawn_V1_All, in about 36 buildings and camps) pick from it, and traders look items up in it")

# ------------------------------------------------------------------ what each kind of point and box is (tooltips)

PANEL_HELP = (
    "The game's switch panel: ignition, starter, fuel pump, fan and light switches, and the horn. Add puts the vanilla "
    "panel here, with the Ignition and Headlight switch boxes on its switches.\n"
    "A panel of your own: pick your panel mesh as the point (eyedropper). Its origin and rotation are the panel's, and "
    "it's drawn instead of the vanilla one. Parent to it objects named switch_ignition, switch_start, switch_fuelpump, "
    "switch_fan, switch_lights and horn (Blender's .001 endings are fine): each puts that switch at its origin, "
    "turning about it when flipped, and a mesh is drawn instead of the vanilla switch (an empty keeps the vanilla one). "
    "Switches you leave out stay where the vanilla panel has them. The Headlight switch box is still where the light "
    "switch is clicked, the Ignition box where the key is turned")

HELP = {
    "engine": "Where the engine sits: the game's engine mount (the Junker's, front-engined). Engines fit on it as on "
              "the game's cars. Turning the marker turns the mount and the engine",
    "radiator": "The radiator mount. Radiators fit on it, and the engine's cooling goes through it",
    "exhaust": "The exhaust bracket. Exhausts fit on it",
    "wheel": "A wheel's centre at rest. The hub is drawn above it, at the top of the suspension, as the game has it.\n"
             "Power: the engine drives this wheel (through a differential). Steering: it turns with the steering wheel. "
             "Travel, stiffness and absorption are in Properties > Suspension",
    "gearlever": "The shift lever's place",
    "suspension": "The slot the game's lift kit (suspension_lift_kit) is fitted to, as on the game's cars. Fitted, "
                  "the car switches to its lifted suspension: longer travel, softer, wheels further out (set in Properties > "
                  "Suspension > Lifted).\nStock suspension: shown until the kit is fitted, then hidden. Lifted suspension: "
                  "hidden until it's fitted, then shown. Pick objects of your model (or game ones from Decorative > Import "
                  "game car part). Without this point the slot goes between the front wheels",
    "steeringwheel": "The game's steering column, with the steering wheel's point at its end. Moving or turning the "
                     "column takes the point along; move the point itself to set where the wheel sits. Pick an object "
                     "instead to have no column (the point alone)",
    "switchpanel": PANEL_HELP,
    "handbrake": "The game's handbrake lever: the player clicks it (or presses the handbrake key) to pull and release it. "
                 "It pivots about the marker, the way the marker faces (on 40°, off 70°, about its X as the game has it). "
                 "Without one the handbrake still works from the key, unseen",
    "toolbox": "The game's toolset kept in the car (the game calls it \"Toolset\": use with F)",
    "adjuster": "The game's part adjuster toolset carried in the car (\"Part Adjuster Toolset\": use with F), the "
                "tool that moves and turns attachment points",
    "fuelcap": "The fuel filler cap: where the car is filled from cans and syphoned (the game's fuel filler, with its "
               "fuel level and use). The tank's size is in Properties > Fuel",
    "fueltank": "A fuel tank to look at (the game's cars keep their fuel in the filler: see Fuel cap)",
    "radio": "The radio slot",
    "seat": "A seat's place: the first two front ones are driver and passenger, the ones further back rear seats",
    "door": "A door's hinge: the door hangs from its front edge here and opens outwards",
    "trunk": "The trunk lid's hinge: the lid hangs from its front edge here and opens upwards",
    "hood": "The hood's hinge: the hood hangs from its rear edge here, flush on top, and opens upwards",
    "bumper": "A bumper's place: in front of the car's middle it's a front bumper, behind it a rear one",
    "window": "The windscreen's place",
    "roofrack": "The roof rack's place",
    "headlight": "A headlight's place",
    "ignition": "The ignition: the box where the player turns the key. Added with the switch panel, on its ignition switch",
    "headlights": "The light switch's box, where the player clicks to switch the headlights. Added with the switch "
                  "panel, on its light switch",
    "frontpanel": "The dashboard: where gauges are fitted (speedometer, rev counter, fuel, temperature...). Hold a "
                  "gauge inside this box and use it: it's fitted where you hold it, as on the game's dashboards. Without "
                  "one, the game's slot is put by the steering wheel",
    "attack": "Ram damage: the box that hurts what the car runs into, as the game's cars do (their CarAttack). Enemies "
              "it touches while the car goes faster than 5 take the speed times 3 in damage, or times the front bumper's "
              "damage when one is fitted. Put it over the front. Without one, it's put across the front of the body",
    "driver": "The driver's seat. Seat: the sphere the player looks at to get in (the game's, on the seat: its size is "
              "the sphere's). Driver's view: the eyes, where the camera is when sitting, facing the car's front (Preview "
              "looks through it). Exit: where the player gets out. Added with the driver's seat attachment point too",
    "solid": "What makes the car solid: boxes (a cube empty, or a mesh by its box) or a mesh itself (Mesh for "
             "collision: blown up a little, as its convex hull). Each is drawn in green as the game will have it. "
             "Hidden: collision only, not drawn in game",
    "physlock": "The physics lock: items dropped inside these boxes are locked to the car, so they ride along instead of "
                "sliding or falling out as it drives (the game's cars lock their beds and cabins this way)",
    "com": "The car's centre of mass: replaces the default (without it, the Poloska's moved to this body). Lower and "
           "nearer the middle is steadier",
}
