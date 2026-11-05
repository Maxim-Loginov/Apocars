Apocars 0.7.5 - a vehicle and car part injector for Apocalypter (BepInEx 5).

Install: extract the Apocars folder into Apocalypter/BepInEx/plugins (so you have BepInEx/plugins/Apocars/Apocars.dll).

Assets: Apocars builds every vehicle and part it finds in BepInEx/plugins/<any mod>/apocars/*.lua (+ .glb), made with
the Apocars Blender add-on (its Hot export writes them there). With HotExport on (BepInEx/config), an asset saved while
the game runs is rebuilt within a second and its copies respawn where they are.

Config (BepInEx/config/com.apocalypter.apocars.cfg):
  General.HotExport     rebuild assets when their files change (default on)
  Tools.DumpVanilla     in a loaded game: write the game's vehicles and parts to BepInEx/Apocars-vanilla (the add-on's
                        vanilla library is built from this dump)
  Tools.Spawn           the id of an Apocars vehicle or part: a copy is spawned in front of you

Each build writes a report to BepInEx/Apocars-<id>.txt.
MoonSharp (MIT, see MoonSharp.LICENSE.txt) reads the .lua files.
