# Apocars: make Apocalypter vehicles and car parts in Blender for the Apocars injector (BepInEx), or build them into
# mods of their own. The steps are in the 3D view's sidebar (N), tab Apocars.
from . import build, colliders, decor, export, library, meshparts, ops, props, ui   # noqa: F401  (build is used by export)

modules = (props, ops, decor, colliders, meshparts, export, library, ui)


def register():
    for m in modules:
        m.register()


def unregister():
    for m in reversed(modules):
        m.unregister()
