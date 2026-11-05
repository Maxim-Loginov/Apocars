# What a solid collider is in game, drawn in the viewport: a wireframe under the solid's object (never exported).
#   Mesh for collision: the mesh blown up by Upscale (as Apocars does it), as the convex hull a car's collider is.
#   A mesh by its box: its bounding box.  A cube empty is drawn by Blender already.
import bmesh
import bpy
from mathutils import Matrix, Vector

ROLE = "apocars"


def _remove(o):
    data = o.data
    bpy.data.objects.remove(o, do_unlink=True)
    if data is not None and data.users == 0:
        bpy.data.meshes.remove(data)


def clear(obj):
    for c in list(obj.children):
        if c.get(ROLE) == "collision":
            _remove(c)


def refresh(sol):
    o = sol.obj
    if o is None:
        return
    clear(o)
    if getattr(sol, "split", 0) > 0:
        return            # split into pieces: they're drawn themselves
    if o.type != "MESH" or not o.data.vertices:
        return
    vs = [v.co.copy() for v in o.data.vertices]
    lo = Vector(min(v[k] for v in vs) for k in range(3))
    hi = Vector(max(v[k] for v in vs) for k in range(3))
    c, ext = (lo + hi) / 2, (hi - lo) / 2
    bm = bmesh.new()
    if sol.mesh:
        f = [1 + sol.inflate / max(ext[k], 0.01) for k in range(3)]
        for v in vs:
            d = v - c
            bm.verts.new(c + Vector((d.x * f[0], d.y * f[1], d.z * f[2])))
        bmesh.ops.convex_hull(bm, input=bm.verts[:])
        # what's left inside the hull (or on no face of it) goes: each once
        loose = [v for v in bm.verts if not v.link_faces]
        if loose:
            bmesh.ops.delete(bm, geom=loose, context="VERTS")
        if not bm.faces:   # flat: no hull, its box (as thin as it is)
            bm.clear()
            bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.LocRotScale(c, None, Vector(max(ext[k] * 2 * f[k], 0.001) for k in range(3))))
    else:
        bmesh.ops.create_cube(bm, size=1.0, matrix=Matrix.LocRotScale(c, None, ext * 2))
    me = bpy.data.meshes.new(o.name + " collision")
    bm.to_mesh(me)
    bm.free()
    co = bpy.data.objects.new(o.name + " (collision)", me)
    for coll in o.users_collection:
        coll.objects.link(co)
    co.parent = o
    co.matrix_parent_inverse = Matrix.Identity(4)
    co.display_type = "WIRE"
    co.show_in_front = True
    co.hide_render = True
    co.hide_select = True
    co.color = (0.25, 1.0, 0.35, 1.0)
    co[ROLE] = "collision"


def refresh_all(s):
    for sol in s.solids:
        try:
            refresh(sol)
        except Exception:
            pass


def visuals(sol):
    """What draws a solid in the viewport: its wireframes and pieces, or the box empty itself."""
    o = sol.obj
    if o is None:
        return []
    out = [c for c in o.children if c.get(ROLE) in ("collision", "hull")]
    if o.type == "EMPTY":
        out.append(o)
    return out


def show(sol, on):
    for v in visuals(sol):
        try:
            v.hide_set(not on)
        except RuntimeError:
            pass


def clear_all(obj):
    """Every collider drawing and piece hung on an object."""
    for c in list(obj.children):
        if c.get(ROLE) in ("collision", "hull"):
            _remove(c)
