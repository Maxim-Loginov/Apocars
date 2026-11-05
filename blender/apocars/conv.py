# Unity's axes and Blender's. Blender: Z up, a car's front towards -Y, its left towards +X; the glTF exporter turns that
# into glTF's (Y up, +Z front), which Apocars reads into Unity's (Y up, +Z front, left-handed: X mirrored).
#   Unity (x, y, z)  ->  Blender (-x, -z, y)
from mathutils import Matrix, Quaternion, Vector

C = Matrix(((-1, 0, 0), (0, 0, -1), (0, 1, 0)))   # Unity -> Blender
CT = C.transposed()                                # Blender -> Unity (C is orthogonal)


def pos(u):
    return Vector((-u[0], -u[2], u[1]))


def unity_pos(b):
    return (-b[0], b[2], -b[1])


def size(u):
    return Vector((abs(u[0]), abs(u[2]), abs(u[1])))


def rot(q):
    """A Unity rotation (x, y, z, w) as a Blender 3x3 matrix."""
    x, y, z, w = q
    m = Quaternion((w, x, y, z)).to_matrix()
    return C @ m @ CT


def unity_vec(v):
    return Vector((-v[0], v[2], -v[1]))
