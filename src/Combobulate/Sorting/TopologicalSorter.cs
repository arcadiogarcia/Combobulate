using System;
using System.Numerics;
using Combobulate.Caching;

namespace Combobulate.Sorting;

/// <summary>
/// Wraps the original O(n²) "any vertex behind plane" predecessor test +
/// Kahn topological sort. Documented limitation: when two quads mutually
/// straddle each other's planes (a 2-cycle in the predecessor graph),
/// the sort cannot resolve them and falls back to centroid-Z, which
/// produces incorrect ordering for some viewing angles. See
/// <see cref="NewellSorter"/> and <see cref="BspSorter"/> for cycle-free
/// alternatives.
/// </summary>
public sealed class TopologicalSorter : IFaceSorter
{
    private readonly ObjGeometry _geometry;
    private readonly int[][] _predecessors;

    public TopologicalSorter(ObjGeometry geometry)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _predecessors = geometry.Predecessors;
    }

    public int QuadCount => _geometry.Quads.Length;

    public int Sort(Matrix4x4 rotation, int[] orderBuffer, bool[] visibleBuffer, float cameraDistance = 0f, float cullMarginCos = 0f)
    {
        var quads = _geometry.Quads;
        int n = quads.Length;
        if (n == 0) return 0;

        // Cull: a quad is visible iff its rotated normal points toward the camera. Under
        // perspective (cameraDistance > 0) the test uses the per-face view ray from the
        // camera to the face centroid; under orthographic it reduces to viewNormal.Z > eps.
        // cullMarginCos > 0 widens the front-facing cone by asin(cullMarginCos) to absorb
        // small CPU-vs-GPU rotation mismatches during animations.
        Span<int> visIdx = n <= 64 ? stackalloc int[n] : new int[n];
        int visCount = 0;
        bool persp = cameraDistance > 0f;
        for (int i = 0; i < n; i++)
        {
            var rn = Vector3.TransformNormal(quads[i].Normal, rotation);
            // Cosine-scale cull (see GeometryPredicates.IsFrontFacing[Perspective]):
            // rejects edge-on faces where cos(θ) noise would otherwise let the face slip
            // through and smear its texture across the screen.
            bool vis;
            if (persp)
            {
                var rc = Vector3.Transform(quads[i].Centroid, rotation);
                vis = GeometryPredicates.IsFrontFacingPerspective(rn, rc, cameraDistance, cullMarginCos);
            }
            else
            {
                vis = GeometryPredicates.IsFrontFacing(rn.Z, cullMarginCos);
            }
            visibleBuffer[i] = vis;
            if (vis) visIdx[visCount++] = i;
        }
        if (visCount == 0) return 0;
        if (visCount == 1) { orderBuffer[0] = visIdx[0]; return 1; }

        // Map cached-quad index → slot index in the visible subset.
        Span<int> slot = n <= 64 ? stackalloc int[n] : new int[n];
        for (int i = 0; i < n; i++) slot[i] = -1;
        for (int i = 0; i < visCount; i++) slot[visIdx[i]] = i;

        Span<int>   inDegree = visCount <= 64 ? stackalloc int[visCount]   : new int[visCount];
        Span<float> viewZ    = visCount <= 64 ? stackalloc float[visCount] : new float[visCount];
        Span<bool>  emitted  = visCount <= 64 ? stackalloc bool[visCount]  : new bool[visCount];
        for (int i = 0; i < visCount; i++)
        {
            var q = quads[visIdx[i]];
            viewZ[i] = Vector3.Transform(q.Centroid, rotation).Z;
            var preds = _predecessors[visIdx[i]];
            int deg = 0;
            for (int p = 0; p < preds.Length; p++)
                if (slot[preds[p]] >= 0) deg++;
            inDegree[i] = deg;
        }

        for (int step = 0; step < visCount; step++)
        {
            int pick = -1;
            float pickZ = float.PositiveInfinity;
            for (int i = 0; i < visCount; i++)
            {
                if (emitted[i] || inDegree[i] != 0) continue;
                if (viewZ[i] < pickZ) { pickZ = viewZ[i]; pick = i; }
            }

            if (pick < 0)
            {
                // Cycle: fall back to view-Z over remaining unemitted quads.
                for (int i = 0; i < visCount; i++)
                {
                    if (emitted[i]) continue;
                    if (viewZ[i] < pickZ) { pickZ = viewZ[i]; pick = i; }
                }
                if (pick < 0) break;
            }

            emitted[pick] = true;
            orderBuffer[step] = visIdx[pick];
            int picked = visIdx[pick];
            for (int j = 0; j < visCount; j++)
            {
                if (emitted[j]) continue;
                var preds = _predecessors[visIdx[j]];
                for (int k = 0; k < preds.Length; k++)
                    if (preds[k] == picked) { inDegree[j]--; break; }
            }
        }

        return visCount;
    }
}
