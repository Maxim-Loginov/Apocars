<p align="center"><img src="icon.png" width="128" alt="Apocars icon"></p>

# Apocars

**Make your own vehicles and car parts for Apocalypter in Blender and drive them in game.**

Apocars has two halves:

- **The mod** (`runtime/`, a BepInEx 5 plugin) builds vehicles and parts from `.glb` models and `.lua` descriptions. It builds them on top of the game's own cars, so they drive, burn fuel, take parts and break like the real thing.
- **The Blender add-on** (`blender/apocars/`) walks you through making one, step by step. It exports the model straight into the game, where the car rebuilds in about a second while you play.

> [!NOTE]
> Apocars is an unofficial fan mod. It ships no game files. Everything it takes from the game (parts, meshes, materials) is read from your own installed copy.

---

## Main features

### In game (the mod)

- **Real game vehicles underneath.** Every car is built on a vanilla one, with its systems, FSMs and IDs. Driving, fuel, damage, part fitting, the radio and headlights all work the way the game does them.
- **Hot reload.** Save from Blender while the game is running, and the car is rebuilt within a second. Copies already in the world respawn where they stand and keep their fitted parts. A car you're sitting in waits until you get out.
- **Attachment points for every game part:** engine, radiator, exhaust, wheels, gear lever, steering column with steering wheel, switch panel (ignition and headlights), handbrake, fuel cap, radio, seats, doors, trunk, hood, bumpers, windows, roof rack, headlights, toolbox and part adjuster. Engine, radiator and exhaust mounts, wheel hubs, the steering column and the switch panel use the game's own meshes.
- **Doors, hoods and trunk lids hinge like the game's.** Any fitted door is measured and hung on its hinge edge, and it opens the right way.
- **Your own parts.** Any mesh can become a part of its own, such as a door, hood or bumper. You can take it off, carry it and fit it again, and it has its own colliders and physics lock. It doesn't collide while you hold it.
- **Collision:**
  - solid boxes, and meshes split into convex pieces;
  - the driver's seat, with separate sit, view and exit spots;
  - the dashboard (where gauges fit);
  - ram damage, which hurts enemies you drive into;
  - physics lock, which holds items dropped in the bed;
  - centre of mass.
- **Drivetrain:**
  - power and steering per wheel, including reversed steering for rear-steer;
  - suspension travel, stiffness and absorption, either for each wheel or for all at once;
  - differentials: open, locked or limited slip, for each axle and between axles;
  - fuel tank size.
- **Spawning.** Your vehicle can appear wherever a chosen game vehicle appears ("spawn like Poloska"). You can also pick spawn lists yourself: camps, bigger camps, the starter base, unique spots, trailers, and merchant, mechanic and medic stock.
- **Game materials.** Name a material after a game material, or pick one in the add-on, and the game's own material is used.
- **A report for every build** in `BepInEx/Apocars-<id>.txt`, listing anything that didn't fit.

### In Blender (the add-on)

- **A step-by-step panel** in the 3D View sidebar, with one step for each stage: What you're making → Body → Decorative → Attachment points → Collision boxes → Properties → Export. Every option has a tooltip.
- **Test buttons** that preview a real game part on an attachment point, so you can see that an engine, wheel or seat fits before going in game.
- **Decorative:**
  - **Import game material** puts a game material on the selected mesh.
  - **Import game car part** adds the game's suspensions, drivetrains, engine bays and fuel tanks as decoration.
- **Colliders from a mesh** with [CoACD](https://github.com/SarahWeiii/CoACD) convex decomposition. Choose how many pieces (70–120 suits a car body). CoACD runs in the background and installs itself the first time.
- **Driver's view preview.** Look through the driver's eyes with the game's field of view, then exit back to where you were.
- **Make mesh into attachment.** Turn a selected mesh into a door, hood, bumper or other part, with its own collision and physics-lock sections.
- **Light exports:**
  - game materials carry no pictures, because the game uses its own;
  - your own pictures are saved once in a shared `textures/` folder, however many parts use them.
- **Build mod** makes a standalone mod with its own ID, name and folder. That mod doesn't need Apocars installed. Build mod also makes a `.zip` of it, ready to share.

---

## Quick guide

### 1. Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64) into the Apocalypter folder and start the game once.
2. **Mod:** from the [Releases](../../releases), extract `Apocars-<version>.zip` into `Apocalypter/BepInEx/plugins`. You should end up with `BepInEx/plugins/Apocars/Apocars.dll`.
3. **Add-on:** in Blender 4.2 or newer, go to **Edit → Preferences → Add-ons → Install from Disk** and pick `Apocars-Blender-addon-<version>.zip`. The panel is in the 3D View sidebar: press **N**, then open the **Apocars** tab.

### 2. Get the game's parts into Blender (once)

The Test buttons, game mounts, game materials and decorations come from your copy of the game.

1. Start the game and load a save.
2. In `BepInEx/config/com.apocalypter.apocars.cfg`, set `Tools.DumpVanilla = true`, or switch it on from an in-game config manager. Apocars writes the game's vehicles and parts to `BepInEx/Apocars-vanilla` and switches the option back off.
3. In Blender, under **Export**, click **Build vanilla library**. It takes a minute or two.

### 3. Make a car

1. **What you're making:** choose **Vehicle**, then give it an ID (letters, digits and `_`), a name and a mod folder. Check that the game folder was found.
2. **Body:** select your car's mesh, click **Use selected** (the eyedropper), then **Set up**.
3. **Attachment points:** press **+** on a row to add a point (an engine, a wheel, a door...) and move it into place. **Test** previews a game part on it. For your own doors, hood and so on, select the mesh and use **Make mesh into attachment**.
4. **Collision boxes:**
   - Add the **Driver's seat** and set its view and exit spots. **Preview** shows the driver's view.
   - Adding a switch panel adds the ignition and headlight boxes on its own.
   - Under **Solid**, use **Add box** for simple shapes, or select a mesh and split it into convex pieces.
5. **Properties:** choose where it spawns, and set up suspension, differentials and the fuel tank size.
6. **Export:**
   - **Hot export** writes the car into the game. If the game is running, the car appears or rebuilds within a second.
   - To spawn one in front of you, set `Tools.Spawn` in the config to your car's ID.
   - **Build mod** makes a standalone mod and a zip to share.

Parts are made the same way: choose **Attachment** in the first step.

---

## Building from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) and an Apocalypter install with BepInEx, whose DLLs the mod is built against.

```sh
python3 scripts/make_release.py "<path to Apocalypter>"
```

The script writes both archives into `dist/`. To build just the mod:

```sh
cd runtime
dotnet build -c Release -p:GameDir="<path to Apocalypter>"
```

### Layout

| Folder | What's in it |
|---|---|
| `runtime/` | The BepInEx plugin (C#). `Assembler.cs` builds a vehicle from a spec, `Parts.cs` builds parts, `Gltf.cs` loads models, `Spawning.cs` handles spawn lists, `Hinges.cs` hangs doors and lids, and `Rebuild.cs` does the hot reload. |
| `blender/apocars/` | The Blender add-on (Python). `ui.py` is the panel, `export.py` writes `.glb` and `.lua`, `build.py` makes standalone mods, `colliders.py` does the CoACD split, and `glbpack.py` keeps exports light. |
| `tools/` | Development scripts: build the add-on's game data from a vanilla dump, read PlayMaker FSMs, build the vanilla library, and make a test car. |
| `scripts/` | `make_release.py` builds both archives. |

### How a vehicle is described

The add-on writes an `<id>.lua` next to `<id>.glb`. The `.lua` returns a table with:

- the model;
- which game vehicle to build on;
- the attachment points (objects in the model, by name);
- boxes, solids, wheels and their suspension;
- differentials, fuel and spawn lists.

Apocars reads every `.lua` in `BepInEx/plugins/<any mod>/apocars/`.

---

## Credits

- [MoonSharp](https://www.moonsharp.org/) (MIT) reads the `.lua` files.
- [CoACD](https://github.com/SarahWeiii/CoACD) (MIT) does the convex decomposition. It's installed by the add-on, not bundled.
- [UnityPy](https://github.com/K0lb3/UnityPy) (MIT) reads the game's files for the vanilla library. It's installed by the add-on, not bundled.

## License

[MIT](LICENSE)
