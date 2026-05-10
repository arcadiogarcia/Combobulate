using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

#if WINAPPSDK
using Windows.UI;
using Microsoft.UI.Composition;
#else
using Windows.UI;
using Windows.UI.Composition;
#endif

namespace Combobulate.Caching;

/// <summary>Per-quad brush + fallback colour binding produced by <see cref="MaterialResolver"/>.</summary>
internal readonly struct QuadBrushBinding
{
    public QuadBrushBinding(CompositionBrush brush, Color fallbackColor)
    {
        Brush = brush;
        FallbackColor = fallbackColor;
    }
    public CompositionBrush Brush { get; }
    public Color FallbackColor { get; }
}

internal sealed class ResolvedQuadMaterials
{
    public ResolvedQuadMaterials(QuadBrushBinding[] bindings) { Bindings = bindings; }
    public QuadBrushBinding[] Bindings { get; }
}

/// <summary>
/// Resolves an <see cref="ObjGeometry"/> + optional <see cref="ObjMaterialPack"/> into per-quad
/// composition brushes. Caches:
/// <list type="bullet">
///   <item>texture surfaces by <see cref="ObjTextureSource.CacheKey"/> so each image is decoded once;</item>
///   <item>per-(geometry, pack) brush arrays so identical XAML pages reuse one binding set.</item>
/// </list>
/// Live texture updates flow through <see cref="ObjTextureSource.Invalidated"/> to repoint the
/// cached surface brushes — no Combobulate.Rebuild required.
/// </summary>
internal static class MaterialResolver
{
    private sealed class TextureEntry
    {
        public ICompositionSurface? Surface;
        public readonly List<WeakReference<CompositionSurfaceBrush>> Brushes = new();
        public readonly object Gate = new();
    }

    private static readonly ConcurrentDictionary<string, TextureEntry> _textures =
        new(StringComparer.Ordinal);

    // Per-geometry → per-pack (or per-"no pack" sentinel) → bindings. ConditionalWeakTable
    // ensures entries die with their geometry; nested table does the same for pack.
    private static readonly ConditionalWeakTable<ObjGeometry, ConditionalWeakTable<object, ResolvedQuadMaterials>> _byGeometry = new();
    private static readonly object _noPackSentinel = new();

    public static void ClearTextures() => _textures.Clear();

    public static ResolvedQuadMaterials Resolve(Compositor compositor, ObjGeometry geometry, ObjMaterialPack? pack)
    {
        if (compositor == null) throw new ArgumentNullException(nameof(compositor));
        if (geometry == null) throw new ArgumentNullException(nameof(geometry));

        var packKey = (object?)pack ?? _noPackSentinel;
        var nested = _byGeometry.GetValue(geometry, _ => new ConditionalWeakTable<object, ResolvedQuadMaterials>());
        if (nested.TryGetValue(packKey, out var existing)) return existing;

        var resolved = ResolveUnique(compositor, geometry, pack);
        nested.Add(packKey, resolved);
        return resolved;
    }

    public static ResolvedQuadMaterials ResolveUnique(Compositor compositor, ObjGeometry geometry, ObjMaterialPack? pack)
    {
        if (compositor == null) throw new ArgumentNullException(nameof(compositor));
        if (geometry == null) throw new ArgumentNullException(nameof(geometry));

        var quads = geometry.Quads;
        var bindings = new QuadBrushBinding[quads.Length];
        for (int i = 0; i < quads.Length; i++)
        {
            var q = quads[i];
            bindings[i] = CreateBinding(compositor, q, ResolveMaterial(q, pack));
        }

        return new ResolvedQuadMaterials(bindings);
    }

    public static int[] UpdateMaterialSlots(
        Compositor compositor,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        IReadOnlyDictionary<string, ObjMaterial> materials)
    {
        if (compositor == null) throw new ArgumentNullException(nameof(compositor));
        if (geometry == null) throw new ArgumentNullException(nameof(geometry));
        if (bindings == null) throw new ArgumentNullException(nameof(bindings));
        if (materials == null) throw new ArgumentNullException(nameof(materials));
        if (bindings.Bindings.Length != geometry.Quads.Length)
            throw new ArgumentException("Binding count must match geometry quad count.", nameof(bindings));

        var changed = new List<int>();
        var quads = geometry.Quads;
        for (int i = 0; i < quads.Length; i++)
        {
            var materialName = quads[i].MaterialName;
            if (materialName == null || !materials.TryGetValue(materialName, out var material))
                continue;

            var updated = UpdateBinding(compositor, quads[i], material, bindings.Bindings[i]);
            bindings.Bindings[i] = updated;
            changed.Add(i);
        }

        return changed.ToArray();
    }

    private static ObjMaterial? ResolveMaterial(CachedQuad quad, ObjMaterialPack? pack)
    {
        if (pack == null) return null;
        ObjMaterial? material = null;
        if (quad.MaterialName != null)
            pack.Materials.TryGetValue(quad.MaterialName, out material);
        return material ?? pack.Fallback;
    }

    private static QuadBrushBinding CreateBinding(Compositor compositor, CachedQuad quad, ObjMaterial? material)
    {
        var fallback = quad.FallbackColor;
        CompositionBrush brush;
        if (material?.DiffuseTexture != null)
        {
            var surfaceBrush = compositor.CreateSurfaceBrush();
            ApplySurfaceBrush(compositor, surfaceBrush, quad, material);
            brush = surfaceBrush;
            if (material.DiffuseColor is { } tint)
                fallback = tint;
        }
        else if (material?.DiffuseColor is { } solid)
        {
            brush = compositor.CreateColorBrush(solid);
            fallback = solid;
        }
        else
        {
            brush = compositor.CreateColorBrush(fallback);
        }

        return new QuadBrushBinding(brush, fallback);
    }

    private static QuadBrushBinding UpdateBinding(
        Compositor compositor,
        CachedQuad quad,
        ObjMaterial material,
        QuadBrushBinding existing)
    {
        var fallback = material.DiffuseColor ?? quad.FallbackColor;
        if (material.DiffuseTexture != null)
        {
            if (existing.Brush is CompositionSurfaceBrush surfaceBrush)
            {
                ApplySurfaceBrush(compositor, surfaceBrush, quad, material);
                return new QuadBrushBinding(surfaceBrush, fallback);
            }

            return CreateBinding(compositor, quad, material);
        }

        if (existing.Brush is CompositionColorBrush colorBrush)
        {
            colorBrush.Color = fallback;
            return new QuadBrushBinding(colorBrush, fallback);
        }

        return new QuadBrushBinding(compositor.CreateColorBrush(fallback), fallback);
    }

    private static void ApplySurfaceBrush(
        Compositor compositor,
        CompositionSurfaceBrush surfaceBrush,
        CachedQuad quad,
        ObjMaterial material)
    {
        surfaceBrush.Stretch = CompositionStretch.Fill;
        surfaceBrush.HorizontalAlignmentRatio = 0;
        surfaceBrush.VerticalAlignmentRatio = 0;
        surfaceBrush.TransformMatrix = BuildBrushTransform(quad, material);
        surfaceBrush.Surface = null;
        GetOrLoadSurface(compositor, material.DiffuseTexture!, surfaceBrush);
    }

    private static void GetOrLoadSurface(Compositor compositor, ObjTextureSource source, CompositionSurfaceBrush brush)
    {
        var entry = _textures.GetOrAdd(source.CacheKey, key =>
        {
            var e = new TextureEntry();
            source.Invalidated -= OnSourceInvalidated;
            source.Invalidated += OnSourceInvalidated;
            // Kick off load; assignment to e.Surface happens once awaited.
            _ = LoadAsync(compositor, source, e);
            return e;
        });

        lock (entry.Gate)
        {
            entry.Brushes.Add(new WeakReference<CompositionSurfaceBrush>(brush));
            if (entry.Surface != null)
                brush.Surface = entry.Surface;
        }
    }

    private static async Task LoadAsync(Compositor compositor, ObjTextureSource source, TextureEntry entry)
    {
        var surface = await source.CreateSurfaceAsync(compositor).ConfigureAwait(true);
        lock (entry.Gate)
        {
            entry.Surface = surface;
            PointBrushesAt(entry, surface);
        }
    }

    private static void OnSourceInvalidated(object? sender, EventArgs e)
    {
        if (sender is not ObjTextureSource src) return;
        if (!_textures.TryGetValue(src.CacheKey, out var entry)) return;

        // Kick off a fresh surface load and repoint brushes once it's ready.
        _ = ReloadAsync(src, entry);
    }

    private static async Task ReloadAsync(ObjTextureSource source, TextureEntry entry)
    {
        // We need a Compositor; reuse the first live brush's compositor.
        Compositor? compositor = null;
        lock (entry.Gate)
        {
            foreach (var wr in entry.Brushes)
            {
                if (wr.TryGetTarget(out var b)) { compositor = b.Compositor; break; }
            }
        }
        if (compositor == null) return;

        var surface = await source.CreateSurfaceAsync(compositor).ConfigureAwait(true);
        lock (entry.Gate)
        {
            entry.Surface = surface;
            PointBrushesAt(entry, surface);
        }
    }

    private static void PointBrushesAt(TextureEntry entry, ICompositionSurface surface)
    {
        for (int i = entry.Brushes.Count - 1; i >= 0; i--)
        {
            if (entry.Brushes[i].TryGetTarget(out var brush))
                brush.Surface = surface;
            else
                entry.Brushes.RemoveAt(i);
        }
    }

    private static Matrix3x2 BuildBrushTransform(CachedQuad q, ObjMaterial material)
    {
        // Compute axis-aligned UV bounds and V-flip (OBJ origin = bottom-left, image = top-left).
        var (uMin, vMin, uMax, vMax) = ComputeCrop(q);
        var width = MathF.Max(uMax - uMin, 1e-6f);
        var height = MathF.Max(vMax - vMin, 1e-6f);

        // Apply material UV scale/offset on top.
        var sx = width * material.UvScale.X;
        var sy = height * material.UvScale.Y;
        var tx = uMin + material.UvOffset.X;
        var ty = (1f - vMax) + material.UvOffset.Y; // V-flip

        return new Matrix3x2(sx, 0, 0, sy, tx, ty);
    }

    private static (float uMin, float vMin, float uMax, float vMax) ComputeCrop(CachedQuad q)
    {
        var u0 = q.Uv0; var u1 = q.Uv1; var u2 = q.Uv2; var u3 = q.Uv3;
        var uMin = MathF.Min(MathF.Min(u0.X, u1.X), MathF.Min(u2.X, u3.X));
        var uMax = MathF.Max(MathF.Max(u0.X, u1.X), MathF.Max(u2.X, u3.X));
        var vMin = MathF.Min(MathF.Min(u0.Y, u1.Y), MathF.Min(u2.Y, u3.Y));
        var vMax = MathF.Max(MathF.Max(u0.Y, u1.Y), MathF.Max(u2.Y, u3.Y));
        return (uMin, vMin, uMax, vMax);
    }
}
