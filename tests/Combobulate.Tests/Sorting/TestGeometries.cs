using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Fixtures that build small synthetic <see cref="ObjGeometry"/>
/// instances directly (without going through ObjParser) for sorter tests.
/// Each fixture documents the geometric arrangement and which
/// "ground truth" depth orderings are correct for known view directions.
/// </summary>
internal static class TestGeometries
{
    /// <summary>
    /// Build an ObjGeometry from raw quad vertices. UVs default to per-face
    /// unit-square mapping (matching Combobulate's default when no vt entries).
    /// </summary>
    public static ObjGeometry Build(params Vector3[][] quads)
    {
        var model = new ObjModel();
        foreach (var q in quads)
        {
            int baseIdx = model.Positions.Count;
            foreach (var v in q) model.Positions.Add(new Vector4(v.X, v.Y, v.Z, 1f));
            model.Quads.Add(new ObjQuad(
                new ObjVertex(baseIdx + 0, null, null),
                new ObjVertex(baseIdx + 1, null, null),
                new ObjVertex(baseIdx + 2, null, null),
                new ObjVertex(baseIdx + 3, null, null),
                objectName: null,
                groups: new[] { "default" },
                material: null,
                smoothingGroup: 0));
        }
        return ObjGeometry.Build(model);
    }

    /// <summary>
    /// Front and back faces of a unit cube along the Z axis.
    /// quad 0: front (z=+0.5), normal +Z.
    /// quad 1: back  (z=-0.5), normal -Z.
    /// </summary>
    public static ObjGeometry FrontAndBackQuads()
    {
        return Build(
            // front +Z, CCW from outside
            new[]
            {
                new Vector3(-0.5f, -0.5f, +0.5f),
                new Vector3(+0.5f, -0.5f, +0.5f),
                new Vector3(+0.5f, +0.5f, +0.5f),
                new Vector3(-0.5f, +0.5f, +0.5f),
            },
            // back -Z, CCW from outside (so winding from -Z side is reversed when viewed from +Z)
            new[]
            {
                new Vector3(+0.5f, -0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(-0.5f, +0.5f, -0.5f),
                new Vector3(+0.5f, +0.5f, -0.5f),
            });
    }

    /// <summary>
    /// Six unit-cube faces, all normals pointing outward. Quad order:
    /// 0: +Z front, 1: -Z back, 2: -X left, 3: +X right, 4: +Y top, 5: -Y bottom.
    /// </summary>
    public static ObjGeometry UnitCube()
    {
        return Build(
            // 0: +Z (front)
            new[] { new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, +0.5f), new Vector3(+0.5f, +0.5f, +0.5f), new Vector3(-0.5f, +0.5f, +0.5f) },
            // 1: -Z (back)
            new[] { new Vector3(+0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, +0.5f, -0.5f), new Vector3(+0.5f, +0.5f, -0.5f) },
            // 2: -X (left)
            new[] { new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, +0.5f), new Vector3(-0.5f, +0.5f, +0.5f), new Vector3(-0.5f, +0.5f, -0.5f) },
            // 3: +X (right)
            new[] { new Vector3(+0.5f, -0.5f, +0.5f), new Vector3(+0.5f, -0.5f, -0.5f), new Vector3(+0.5f, +0.5f, -0.5f), new Vector3(+0.5f, +0.5f, +0.5f) },
            // 4: +Y (top)
            new[] { new Vector3(-0.5f, +0.5f, +0.5f), new Vector3(+0.5f, +0.5f, +0.5f), new Vector3(+0.5f, +0.5f, -0.5f), new Vector3(-0.5f, +0.5f, -0.5f) },
            // 5: -Y (bottom)
            new[] { new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(+0.5f, -0.5f, -0.5f), new Vector3(+0.5f, -0.5f, +0.5f), new Vector3(-0.5f, -0.5f, +0.5f) });
    }

    /// <summary>
    /// Two mutually-straddling quads: a +Z plane and a +Y plane that intersect.
    /// This is the cycle case that breaks the original TopologicalSorter:
    ///   - quad A (z=+0.1, normal +Z) — vertices at y±0.7 straddle quad B's plane (y=+0.66).
    ///   - quad B (y=+0.66, normal +Y) — vertices at z±0.099 straddle quad A's plane (z=+0.1).
    /// Designed to mirror our real Deet book bug.
    /// </summary>
    public static ObjGeometry MutualStraddleCycle()
    {
        return Build(
            // A: +Z cover at z=+0.1, big in XY
            new[]
            {
                new Vector3(-0.5f, -0.7f, +0.1f),
                new Vector3(+0.5f, -0.7f, +0.1f),
                new Vector3(+0.5f, +0.7f, +0.1f),
                new Vector3(-0.5f, +0.7f, +0.1f),
            },
            // B: +Y page top strip at y=+0.66, thin in Y, with z ranging ±0.099
            new[]
            {
                new Vector3(-0.5f, +0.66f, -0.099f),
                new Vector3(+0.46f, +0.66f, -0.099f),
                new Vector3(+0.46f, +0.66f, +0.099f),
                new Vector3(-0.5f, +0.66f, +0.099f),
            });
    }

    /// <summary>
    /// Two parallel CCW-from-+Z quads at z=+0.5 and z=-0.5 with opposite normals.
    /// Useful for testing back-face culling (only one is visible from any view).
    /// </summary>
    public static ObjGeometry BackToBackQuads() => FrontAndBackQuads();
}

/// <summary>
/// Helpers that all sorter tests use.
/// </summary>
internal static class SortAssertions
{
    /// <summary>Build a yaw-pitch rotation matrix matching Combobulate's view convention.</summary>
    public static Matrix4x4 Yaw(float deg) =>
        Matrix4x4.CreateRotationY(deg * (System.MathF.PI / 180f));

    public static Matrix4x4 Pitch(float deg) =>
        Matrix4x4.CreateRotationX(deg * (System.MathF.PI / 180f));

    public static Matrix4x4 YawPitch(float yawDeg, float pitchDeg) =>
        Matrix4x4.CreateRotationY(yawDeg * (System.MathF.PI / 180f)) *
        Matrix4x4.CreateRotationX(pitchDeg * (System.MathF.PI / 180f));

    /// <summary>
    /// Verify that the sorter's order is back-to-front by view-Z centroids: every
    /// emitted quad's view-space centroid Z must be ≤ the next emitted quad's.
    /// Allows ε to absorb floating-point noise when centroids tie.
    /// </summary>
    public static void AssertBackToFront(ObjGeometry geometry, int[] order, int count, Matrix4x4 rotation, float epsilon = 1e-3f)
    {
        var quads = geometry.Quads;
        float prevZ = float.NegativeInfinity;
        int prevIdx = -1;
        for (int i = 0; i < count; i++)
        {
            var z = Vector3.Transform(quads[order[i]].Centroid, rotation).Z;
            if (z < prevZ - epsilon)
                Assert.Fail($"Order violates back-to-front at i={i}: quad {order[i]} has Z={z}, previous quad {prevIdx} had Z={prevZ}");
            prevZ = z;
            prevIdx = order[i];
        }
    }

    /// <summary>Visible-quad count from a sort result.</summary>
    public static int CountVisible(bool[] visible)
    {
        int n = 0;
        for (int i = 0; i < visible.Length; i++) if (visible[i]) n++;
        return n;
    }
}
