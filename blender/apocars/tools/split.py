#!/usr/bin/env python3
"""A mesh split into convex pieces for colliders: CoACD (Wei et al., "Approximate Convex Decomposition for 3D Meshes
with Collision-Aware Concavity and Tree Search", SIGGRAPH 2022), asked for a number of pieces.

    python split.py <in.npz: v (n,3), f (m,3)> <out.npz> <count> [max hull vertices]

Writes out.npz with v0, f0, v1, f1... (one pair per piece) and n. Needs numpy and coacd (pip install coacd)."""
import sys

import numpy as np


def main():
    src, dst, count = sys.argv[1], sys.argv[2], int(sys.argv[3])
    max_v = int(sys.argv[4]) if len(sys.argv) > 4 else 64
    import coacd
    coacd.set_log_level("error")
    d = np.load(src)
    mesh = coacd.Mesh(d["v"].astype(np.float64), d["f"].astype(np.int32))
    # a low concavity threshold splits finely, then pieces are merged down to the count asked for
    parts = coacd.run_coacd(mesh, threshold=0.02, max_convex_hull=count, preprocess_mode="auto", max_ch_vertex=max_v,
                            decimate=True, merge=True, seed=1)   # each piece at most max_v corners (Unity: 255 triangles)
    out = {"n": np.array(len(parts))}
    for i, (v, f) in enumerate(parts):
        out[f"v{i}"] = np.asarray(v, dtype=np.float32)
        out[f"f{i}"] = np.asarray(f, dtype=np.int32)
    np.savez(dst, **out)
    print(f"{len(parts)} pieces", flush=True)


if __name__ == "__main__":
    main()
