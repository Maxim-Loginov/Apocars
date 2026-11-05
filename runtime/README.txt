Apocars 0.7.6 - a vehicle and car part injector for Apocalypter (BepInEx 5).

Install: extract the Apocars folder into Apocalypter/BepInEx/plugins (so you have BepInEx/plugins/Apocars/Apocars.dll).

Apocars is a tool for making vehicles and is still in development. It's needed for hot reload while you work and to
extract the game's files for the Blender add-on (Blender 5.0+ recommended; Apocasetter recommended for its settings).
To share a vehicle, use the add-on's Build mod: it makes a standalone mod. Players don't need Apocars for it, so you
don't need to list Apocars as a dependency.

Assets: Apocars builds every vehicle and part it finds in BepInEx/plugins/<any mod>/apocars/*.lua (+ .glb), made with
the Apocars Blender add-on (its Hot export writes them there). With HotExport on (BepInEx/config), an asset saved while
the game runs is rebuilt within a second and its copies respawn where they are.

Config (BepInEx/config/com.apocalypter.apocars.cfg, or in Apocasetter):
  General.HotExport     rebuild assets when their files change (default on)
  Tools.DumpVanilla     in a loaded game: write the game's vehicles and parts to BepInEx/Apocars-vanilla (the add-on's
                        vanilla library is built from this dump)
  Tools.Spawn           the id of an Apocars vehicle or part: a copy is spawned in front of you

Each build writes a report to BepInEx/Apocars-<id>.txt.
MoonSharp (MIT, see MoonSharp.LICENSE.txt) reads the .lua files.

Source, issues and feature requests: https://github.com/Maxim-Loginov/Apocars (preferred), or Discord: internet_gremlin
