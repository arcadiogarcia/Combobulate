using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Combobulate.Caching;
using Combobulate.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace Combobulate.Tests;

/// <summary>
/// Validates that, after painter subdivision, EVERY cover fragment (quad or
/// triangle) samples the texture at exactly the parent UV that belongs to its
/// physical position — i.e. the subdivided cover reassembles the original
/// image with no per-fragment rotation, shear, or mirror.
///
/// The test replicates the renderer's exact sprite-basis + brush-matrix
/// pipeline (BakedAspectGraphRenderer.BuildTreeContent +
/// MaterialResolver.BuildBrushTransform) in pure math, then checks the
/// mapping invariant at each fragment corner.
/// </summary>
public class SubdivisionBrushMappingTests
{
    private readonly ITestOutputHelper _o;
    public SubdivisionBrushMappingTests(ITestOutputHelper o) => _o = o;

    // Exact copy of Deet's BookObj (Book3D.cs).
    private const string BookObj = """
        v -0.5 -0.7  0.1
        v  0.5 -0.7  0.1
        v  0.5  0.7  0.1
        v -0.5  0.7  0.1
        v -0.5 -0.7 -0.1
        v  0.5 -0.7 -0.1
        v  0.5  0.7 -0.1
        v -0.5  0.7 -0.1
        v  0.46 -0.66  0.099
        v  0.46  0.66  0.099
        v -0.5   0.66  0.099
        v -0.5  -0.66  0.099
        v  0.46 -0.66 -0.099
        v  0.46  0.66 -0.099
        v -0.5   0.66 -0.099
        v -0.5  -0.66 -0.099

        usemtl front
        f 1 2 3 4
        usemtl back
        f 5 8 7 6
        usemtl spine
        f 1 4 8 5
        usemtl pages
        f 9 13 14 10
        f 11 10 14 15
        f 9 12 16 13
        """;

    private const float Scale = 150f;

    [Theory]
    [InlineData("front")]
    [InlineData("back")]
    [InlineData("spine")]
    public void EveryFragmentSamplesCorrectUv(string material)
    {
        var r = ObjParser.Parse(BookObj);
        Assert.True(r.Success);
        var geo = ObjGeometry.Build(r.Model);
        var sub = geo.WithPainterSubdivision();

        // Parent quad for this material (there is exactly one cover face per material).
        var parent = geo.Quads.Single(q => q.MaterialName == material);

        var sb = new StringBuilder();
        sb.AppendLine($"=== {material}: parent V0={F(parent.V0)} V1={F(parent.V1)} V2={F(parent.V2)} V3={F(parent.V3)}");
        sb.AppendLine($"    parent uv0={F(parent.Uv0)} uv1={F(parent.Uv1)} uv2={F(parent.Uv2)} uv3={F(parent.Uv3)}");

        float worstErr = 0f;
        int fragCount = 0;
        var failures = new List<string>();

        foreach (var frag in sub.Quads.Where(q => q.MaterialName == material))
        {
            fragCount++;
            var err = ValidateFragment(parent, frag, sb, out var detail);
            if (err > worstErr) worstErr = err;
            if (err > 1e-3f) failures.Add(detail);
        }

        sb.AppendLine($"fragments={fragCount} worstErr={worstErr:0.0000}");
        _o.WriteLine(sb.ToString());
        System.IO.File.WriteAllText($@"D:\Combobulate\subdiv-{material}.txt", sb.ToString());

        Assert.True(worstErr < 1e-3f,
            $"{material}: {failures.Count}/{fragCount} fragments mis-sample. worstErr={worstErr:0.0000}\n"
            + string.Join("\n", failures.Take(8)));
    }

    /// <summary>
    /// Replicates BuildTreeContent's sprite basis + BuildBrushTransform, then
    /// checks that each fragment corner samples the parent UV at that physical
    /// point. Returns the worst sampling error (in UV units) for the fragment.
    /// </summary>
    private static float ValidateFragment(CachedQuad parent, CachedQuad frag, StringBuilder sb, out string detail)
    {
        // ---- Renderer sprite basis (BakedAspectGraphRenderer.BuildTreeContent) ----
        var cq = frag.WithCanonicalAxisAlignedUv();
        var origin = Vector3.Zero; // origin cancels out of the relative mapping.
        var v0 = cq.V0 * Scale + origin;
        var v1 = cq.V1 * Scale + origin;
        var v3 = cq.V3 * Scale + origin;
        var xAxis = v1 - v0;
        var yAxis = v3 - v0;
        var lenX = xAxis.Length();
        var lenY = yAxis.Length();
        var spriteSize = new Vector2(lenX > 0 ? lenX : 1f, lenY > 0 ? lenY : 1f);
        var nx = lenX > 0 ? xAxis / lenX : Vector3.UnitX;
        var ny = lenY > 0 ? yAxis / lenY : Vector3.UnitY;

        // ---- Brush matrix (MaterialResolver.BuildBrushTransform) ----
        Matrix3x2 m = cq.IsTriangle
            ? BrushTransformMath.BuildTriangleAffine(
                cq.Uv0, cq.Uv1, cq.Uv2, Vector2.One, Vector2.Zero, spriteSize)
            : BrushTransformMath.BuildQuadAxisAlignedCrop(
                cq.Uv0, cq.Uv1, cq.Uv2, cq.Uv3, Vector2.One, Vector2.Zero, spriteSize);

        // For each fragment vertex: physical model point P and the parent UV
        // that physically belongs there (bilinear inverse on the parent quad).
        // We then compute the sprite-local coordinate that renders at P and run
        // it through the brush matrix; sampleUV must equal V-flipped parentUV(P).
        Vector3[] verts = cq.IsTriangle
            ? new[] { cq.V0, cq.V1, cq.V2 }
            : new[] { cq.V0, cq.V1, cq.V2, cq.V3 };

        float worst = 0f;
        var dsb = new StringBuilder();
        dsb.Append($"  frag tri={cq.IsTriangle} spr=({lenX:0.0}x{lenY:0.0}) ");
        foreach (var P in verts)
        {
            // sprite-local coordinate of P: project (P-V0) onto the (possibly
            // non-orthogonal) sprite basis. For an orthonormal quad basis this
            // is a plain dot; for a triangle's sheared basis we solve the 2x2.
            var local = SolveLocal(P * Scale - v0, nx * 1f, ny * 1f, lenX, lenY);

            // brush-pixel = local * M (row-vector Matrix3x2), sampleUV = brush/spriteSize.
            float bx = local.X * m.M11 + local.Y * m.M21 + m.M31;
            float by = local.X * m.M12 + local.Y * m.M22 + m.M32;
            var sampleUv = new Vector2(bx / spriteSize.X, by / spriteSize.Y);

            // Expected: the parent UV physically at P. The surface loader is
            // v-up (row 0 = image bottom = objUV.y 0), so the sampled surface V
            // equals objUV.y directly — no flip.
            var parentUv = ParentUvAt(parent, P);
            var expected = new Vector2(parentUv.X, parentUv.Y);

            float e = (sampleUv - expected).Length();
            if (e > worst) worst = e;
            dsb.Append($"[P={F(P)} got={F(sampleUv)} exp={F(expected)} e={e:0.000}] ");
        }
        detail = dsb.ToString();
        sb.AppendLine(detail);
        return worst;
    }

    /// <summary>Solve local (a,b) s.t. a*nx + b*ny == d (least squares on the
    /// 3D-embedded 2D basis). Works for orthonormal and sheared bases.</summary>
    private static Vector2 SolveLocal(Vector3 d, Vector3 nx, Vector3 ny, float lenX, float lenY)
    {
        // Basis columns e1=nx, e2=ny. Solve [e1 e2] [a b]^T = d via normal equations.
        float a11 = Vector3.Dot(nx, nx), a12 = Vector3.Dot(nx, ny), a22 = Vector3.Dot(ny, ny);
        float b1 = Vector3.Dot(nx, d), b2 = Vector3.Dot(ny, d);
        float det = a11 * a22 - a12 * a12;
        if (MathF.Abs(det) < 1e-9f) return Vector2.Zero;
        float a = (b1 * a22 - b2 * a12) / det;
        float b = (a11 * b2 - a12 * b1) / det;
        return new Vector2(a, b);
    }

    /// <summary>Parent UV physically at model point P: invert the parent quad's
    /// bilinear position→UV map. The cover faces are planar axis-aligned
    /// rectangles, so U,V are linear in the parent's edge directions.</summary>
    private static Vector2 ParentUvAt(CachedQuad parent, Vector3 P)
    {
        // Parent edges: eU = V1-V0 (Uv0->Uv1), eV = V3-V0 (Uv0->Uv3).
        var eU = parent.V1 - parent.V0;
        var eV = parent.V3 - parent.V0;
        var d = P - parent.V0;
        // Solve d = s*eU + t*eV (least squares in 3D).
        float a11 = Vector3.Dot(eU, eU), a12 = Vector3.Dot(eU, eV), a22 = Vector3.Dot(eV, eV);
        float b1 = Vector3.Dot(eU, d), b2 = Vector3.Dot(eV, d);
        float det = a11 * a22 - a12 * a12;
        float s = (b1 * a22 - b2 * a12) / det;
        float t = (a11 * b2 - a12 * b1) / det;
        var uv = parent.Uv0 + s * (parent.Uv1 - parent.Uv0) + t * (parent.Uv3 - parent.Uv0);
        return uv;
    }

    private static string F(Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";
    private static string F(Vector2 v) => $"({v.X:0.00},{v.Y:0.00})";
}
