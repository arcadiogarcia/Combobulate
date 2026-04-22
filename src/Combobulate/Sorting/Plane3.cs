using System.Numerics;

namespace Combobulate.Sorting;

/// <summary>
/// An infinite plane in 3D space, expressed as <c>Normal · X = D</c> where
/// <see cref="Normal"/> is unit-length. The signed distance of a point to
/// the plane is <c>Normal · point − D</c>; positive values lie on the
/// <see cref="Normal"/> side ("front"), negative values on the opposite
/// side ("back").
///
/// <para>Used by the BSP builder and Newell splitter as the partitioning
/// primitive. Constructed from a polygon's normal + any of its vertices.</para>
/// </summary>
public readonly struct Plane3
{
    /// <summary>Unit-length plane normal.</summary>
    public Vector3 Normal { get; }

    /// <summary>Plane offset along the normal: <c>D = Normal · point</c>.</summary>
    public float D { get; }

    /// <summary>Construct from an already-unit-length normal and signed offset D.</summary>
    public Plane3(Vector3 unitNormal, float d) { Normal = unitNormal; D = d; }

    /// <summary>Construct from a unit normal and any point lying on the plane.</summary>
    public static Plane3 FromPointAndNormal(Vector3 point, Vector3 unitNormal)
        => new(unitNormal, Vector3.Dot(unitNormal, point));

    /// <summary>Construct from three CCW-wound points; normal points toward the viewer for CCW input.</summary>
    public static Plane3 FromTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return new Plane3(n, Vector3.Dot(n, a));
    }

    /// <summary>Signed distance of <paramref name="p"/> from this plane.</summary>
    public float SignedDistance(Vector3 p) => Vector3.Dot(Normal, p) - D;

    /// <summary>
    /// Solves for the parametric position <c>t</c> at which the segment
    /// <paramref name="a"/>→<paramref name="b"/> crosses this plane.
    /// Returns the intersection point. Caller is responsible for ensuring
    /// the segment actually crosses (signed distances at endpoints have
    /// opposite signs); otherwise the result extrapolates.
    /// </summary>
    /// <remarks>
    /// Numerical safety: the divisor <c>da − db</c> is treated as zero
    /// when its magnitude falls below <see cref="DenomEpsilon"/> rather
    /// than only on exact bitwise zero. A literal <c>denom == 0</c> test
    /// would let a tiny-but-nonzero value (e.g. 1e-30 from a near-parallel
    /// segment) feed the divide and produce ±∞ or a huge <c>t</c>; the
    /// subsequent <c>[0,1]</c> clamp would mask that as an endpoint snap,
    /// silently corrupting the intersection geometry. The midpoint
    /// fallback is used in those degenerate cases for the same reason
    /// the caller is expected to guarantee a real crossing: there is no
    /// well-defined intersection to return.
    /// </remarks>
    public Vector3 IntersectSegment(Vector3 a, Vector3 b, out float t)
    {
        var da = SignedDistance(a);
        var db = SignedDistance(b);
        // Magnitude (not exact-zero) test via centralized predicate: a near-parallel
        // segment can produce a tiny-but-nonzero denom that would explode the divide.
        // The predicate returns false (with t = 0.5) in that degenerate case.
        GeometryPredicates.TryComputeSegmentParam(da, db, out t);
        return a + (b - a) * t;
    }

    /// <summary>
    /// Threshold below which the segment-vs-plane denominator
    /// (<c>signedDistance(a) − signedDistance(b)</c>) is treated as
    /// zero in <see cref="IntersectSegment"/>. Same scale as
    /// <see cref="PolygonSplitter.Epsilon"/> — a signed-distance unit
    /// in object space.
    /// </summary>
    public const float DenomEpsilon = GeometryPredicates.DivisorEpsilon;
}
