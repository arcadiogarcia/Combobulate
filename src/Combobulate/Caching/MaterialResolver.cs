using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Effects;

#if WINAPPSDK
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Effects;
#else
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Effects;
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
        /// <summary>
        /// Explicit pin count. Bumped by <see cref="AcquireAsync"/>, decremented by
        /// <see cref="ReleaseTexture"/>. When > 0 the entry will not be evicted from
        /// the cache even if no surface brushes remain bound to it. Used by callers
        /// that want LOD-specific decoded copies (e.g. a high-res focused-cover
        /// variant) with deterministic memory lifetime.
        /// </summary>
        public int PinCount;
        /// <summary>
        /// Completion signal for the in-flight (or completed) decode. Allows
        /// <see cref="AcquireAsync"/> to await the surface even when the entry was
        /// created by a previous brush bind. Set exactly once when Surface lands.
        /// </summary>
        public readonly TaskCompletionSource<ICompositionSurface> Ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly ConcurrentDictionary<string, TextureEntry> _textures =
        new(StringComparer.Ordinal);

    // Maps ObjTextureSource instance → cache key currently pinned by it. Used by
    // ReleaseTexture so callers pass the same source they acquired with (which may
    // be a different instance than the one originally cached under that key).
    private static readonly ConditionalWeakTable<ObjTextureSource, string> _pinnedKeysBySource = new();

    /// <summary>
    /// Cached <see cref="CompositionEffectFactory"/> for the lit-material effect graph.
    /// One per process lifetime — every lit face shares the same factory and creates
    /// its own <see cref="CompositionEffectBrush"/> from it (cheap: ~0.05ms).
    /// </summary>
    private static CompositionEffectFactory? _litEffectFactory;

    // Per-geometry → per-pack (or per-"no pack" sentinel) → bindings. ConditionalWeakTable
    // ensures entries die with their geometry; nested table does the same for pack.
    private static readonly ConditionalWeakTable<ObjGeometry, ConditionalWeakTable<object, ResolvedQuadMaterials>> _byGeometry = new();
    private static readonly object _noPackSentinel = new();

    /// <summary>
    /// Keeps the inner Diffuse + NormalMap surface brushes used by a lit
    /// <see cref="CompositionEffectBrush"/> managed-alive as long as the effect
    /// brush wrapper itself is alive. Without this, the inner texBrush created
    /// inside <see cref="BuildLitBrush"/> goes out of scope after
    /// <c>SetSourceParameter</c> returns — its COM object stays alive (held by
    /// the effect's native graph) but the managed RCW is collectible. The
    /// texture cache only holds a <see cref="WeakReference{T}"/> to the brush,
    /// so once the RCW is GC'd the cache can no longer repoint the brush when
    /// the async surface load finishes, and the cover renders without diffuse.
    /// Tying the lifetime here keeps the WeakReference resolvable for the life
    /// of the rendered effect brush.
    /// </summary>
    private static readonly ConditionalWeakTable<CompositionEffectBrush, object> _litInnerBrushes = new();

    public static void ClearTextures()
    {
        // Snapshot + clear so disposal happens without holding the dictionary lock.
        var snapshot = _textures.ToArray();
        _textures.Clear();
        foreach (var kv in snapshot)
        {
            lock (kv.Value.Gate)
            {
                DisposeIfDisposable(kv.Value.Surface);
                kv.Value.Surface = null;
                kv.Value.PinCount = 0;
                kv.Value.Brushes.Clear();
            }
        }
    }

    /// <summary>
    /// Ensures a decoded surface exists for <paramref name="source"/> and pins the
    /// cache entry so it survives even when no brushes are bound. Returns the
    /// loaded <see cref="ICompositionSurface"/>; the returned task only completes
    /// once the underlying <c>LoadedImageSurface</c> has finished decoding (so the
    /// caller can swap brushes to it without flicker). Always pair with a matching
    /// <see cref="ReleaseTexture"/> call.
    /// </summary>
    public static Task<ICompositionSurface> AcquireAsync(Compositor compositor, ObjTextureSource source)
    {
        if (compositor == null) throw new ArgumentNullException(nameof(compositor));
        if (source == null) throw new ArgumentNullException(nameof(source));

        var key = source.CacheKey;
        var entry = _textures.GetOrAdd(key, _key =>
        {
            var e = new TextureEntry();
            source.Invalidated -= OnSourceInvalidated;
            source.Invalidated += OnSourceInvalidated;
            _ = LoadAsync(compositor, source, e);
            return e;
        });

        Task<ICompositionSurface> readyTask;
        lock (entry.Gate)
        {
            entry.PinCount++;
            // Remember the key associated with THIS source instance — callers may
            // construct a fresh ObjTextureSource (same URI/size) for release, but
            // most use the same instance they passed in. We re-resolve via key in
            // ReleaseTexture anyway, so this table is purely an integrity check.
            _pinnedKeysBySource.Remove(source);
            _pinnedKeysBySource.Add(source, key);
            // If the load already completed before we acquired, the Ready task is
            // already set; otherwise it will complete in LoadAsync.
            readyTask = entry.Ready.Task;
        }
        return readyTask;
    }

    /// <summary>
    /// Releases a pin acquired via <see cref="AcquireAsync"/>. If the pin count
    /// reaches zero AND no live composition brushes still reference the entry's
    /// surface, the entry is evicted from the cache and the underlying
    /// <c>LoadedImageSurface</c> is disposed. Safe to call from any thread.
    /// Callers must repoint any of their own brushes off this surface before
    /// releasing the last pin to avoid the brush going blank.
    /// </summary>
    public static void ReleaseTexture(ObjTextureSource source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        // Prefer the key stamped when we acquired (covers sources whose CacheKey
        // depends on internal state that might have changed). Falls back to a
        // fresh CacheKey read.
        if (!_pinnedKeysBySource.TryGetValue(source, out var key))
            key = source.CacheKey;

        if (!_textures.TryGetValue(key, out var entry)) return;

        bool evict;
        ICompositionSurface? toDispose = null;
        lock (entry.Gate)
        {
            if (entry.PinCount > 0) entry.PinCount--;
            // Prune dead brush refs first so the live-count check is accurate.
            for (int i = entry.Brushes.Count - 1; i >= 0; i--)
            {
                if (!entry.Brushes[i].TryGetTarget(out _))
                    entry.Brushes.RemoveAt(i);
            }
            evict = entry.PinCount == 0 && entry.Brushes.Count == 0;
            if (evict)
            {
                toDispose = entry.Surface;
                entry.Surface = null;
            }
        }

        if (evict)
        {
            // Only remove if the cached entry is still ours (a parallel Acquire
            // could have replaced it; defensive).
            _textures.TryRemove(new KeyValuePair<string, TextureEntry>(key, entry));
            _pinnedKeysBySource.Remove(source);
            DisposeIfDisposable(toDispose);
        }
    }

    private static void DisposeIfDisposable(ICompositionSurface? surface)
    {
        if (surface is IDisposable d)
        {
            try { d.Dispose(); }
            catch { /* surface already cleaned up by composition shutdown */ }
        }
    }

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

        // Lit path: material has a NormalMap → wrap in SceneLightingEffect
        if (material?.NormalMap != null)
        {
            brush = BuildLitBrush(compositor, quad, material);
            if (material.DiffuseColor is { } tint)
                fallback = tint;
        }
        else if (material?.DiffuseTexture != null)
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

        // Lit path: if material now has a NormalMap, always rebuild as a lit brush.
        // The effect brush structure differs from plain surface/color brushes,
        // so we can't just repoint — rebuild from scratch.
        if (material.NormalMap != null)
            return CreateBinding(compositor, quad, material);

        // Unlit paths (unchanged logic)
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
        // Avoid a one-frame flash: only null the existing surface when the new
        // target hasn't finished decoding. If it's already in the cache loaded,
        // GetOrLoadSurface will swap atomically and we never go through blank.
        var source = material.DiffuseTexture!;
        if (!_textures.TryGetValue(source.CacheKey, out var entry) || entry.Surface == null)
        {
            surfaceBrush.Surface = null;
        }
        GetOrLoadSurface(compositor, source, surfaceBrush);
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
        ICompositionSurface? surface = null;
        try
        {
            surface = await source.CreateSurfaceAsync(compositor).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            lock (entry.Gate) entry.Ready.TrySetException(ex);
            return;
        }
        lock (entry.Gate)
        {
            entry.Surface = surface;
            PointBrushesAt(entry, surface);
            entry.Ready.TrySetResult(surface);
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

        ICompositionSurface surface;
        try
        {
            surface = await source.CreateSurfaceAsync(compositor).ConfigureAwait(true);
        }
        catch
        {
            // Old surface is still valid; leave it in place.
            return;
        }

        ICompositionSurface? oldSurface;
        lock (entry.Gate)
        {
            oldSurface = entry.Surface;
            entry.Surface = surface;
            PointBrushesAt(entry, surface);
        }
        // Dispose the previous surface AFTER brushes have been repointed to the
        // new one — otherwise composition would briefly see freed memory.
        if (!ReferenceEquals(oldSurface, surface))
            DisposeIfDisposable(oldSurface);
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

    // ── Lit material support ─────────────────────────────────────────────────

    private static CompositionEffectFactory GetOrCreateLitFactory(Compositor compositor)
    {
        if (_litEffectFactory is not null) return _litEffectFactory;

        var effect = new BlendEffect
        {
            Mode = BlendEffectMode.Multiply,
            Background = new CompositionEffectSourceParameter("Diffuse"),
            Foreground = new SceneLightingEffect
            {
                Name = "Lighting",
                AmbientAmount  = LightingDefaults.DefaultAmbient,
                DiffuseAmount  = LightingDefaults.DefaultDiffuse,
                SpecularAmount = LightingDefaults.DefaultSpecular,
                SpecularShine  = LightingDefaults.DefaultShine,
                NormalMapSource = new CompositionEffectSourceParameter("NormalMap"),
            },
        };

        _litEffectFactory = compositor.CreateEffectFactory(effect, new[]
        {
            "Lighting.AmbientAmount",
            "Lighting.DiffuseAmount",
            "Lighting.SpecularAmount",
            "Lighting.SpecularShine",
        });

        return _litEffectFactory;
    }

    private static CompositionBrush BuildLitBrush(
        Compositor compositor, CachedQuad quad, ObjMaterial material)
    {
        var factory = GetOrCreateLitFactory(compositor);
        var effectBrush = factory.CreateBrush();

        // Inner brushes are passed to the effect via SetSourceParameter, which
        // hands ownership to the native effect graph. The managed wrappers
        // would then be collectible — but the texture cache only WeakReferences
        // them, so a GC before the async surface load completes would leave
        // the cover unrendered. We hold strong refs here, keyed by effectBrush,
        // so the wrappers live as long as the rendered effect brush does.
        CompositionSurfaceBrush? texBrush = null;
        CompositionSurfaceBrush? normalBrush = null;

        // Diffuse source
        if (material.DiffuseTexture != null)
        {
            // Wire the brush to its surface from the start when one is already
            // warm in the texture cache. The effect graph snapshots the inner
            // brush's surface sampler at SetSourceParameter time — if the brush
            // has no surface at that moment, some effect-graph compositions
            // can render with no diffuse and never re-sample even after
            // Surface is later assigned.
            //
            // NOTE: this is defense-in-depth, NOT the load-bearing fix for the
            // focus-mode grey-cover bug. Disambiguation (see plan.md) proved
            // the 900 px CompositionDrawingSurface cap in ObjTextureSource
            // is necessary AND sufficient — covers render correctly even when
            // this lookup is omitted, and conversely fail when the cap is
            // raised regardless of this lookup. We keep the warm-surface
            // binding because it's the architecturally correct thing to do
            // (bind sources before SetSourceParameter) and costs nothing.
            ICompositionSurface? warmSurface = null;
            if (_textures.TryGetValue(material.DiffuseTexture.CacheKey, out var existingEntry))
            {
                lock (existingEntry.Gate)
                {
                    warmSurface = existingEntry.Surface;
                }
            }
            texBrush = warmSurface != null
                ? compositor.CreateSurfaceBrush(warmSurface)
                : compositor.CreateSurfaceBrush();
            ApplySurfaceBrush(compositor, texBrush, quad, material);
            effectBrush.SetSourceParameter("Diffuse", texBrush);
        }
        else
        {
            effectBrush.SetSourceParameter("Diffuse",
                compositor.CreateColorBrush(material.DiffuseColor ?? quad.FallbackColor));
        }

        // Normal map source
        normalBrush = compositor.CreateSurfaceBrush();
        normalBrush.Surface = material.NormalMap;
        normalBrush.Stretch = CompositionStretch.Fill;
        normalBrush.HorizontalAlignmentRatio = 0;
        normalBrush.VerticalAlignmentRatio = 0;
        normalBrush.TransformMatrix = BuildBrushTransform(quad, material);
        effectBrush.SetSourceParameter("NormalMap", normalBrush);

        // Pin the inner brushes' managed wrappers to the effect brush's
        // lifetime (see _litInnerBrushes XML doc).
        _litInnerBrushes.Add(
            effectBrush,
            texBrush != null
                ? new CompositionSurfaceBrush[] { texBrush, normalBrush }
                : new CompositionSurfaceBrush[] { normalBrush });

        // Bind animatable lighting scalars to the shared LightingDefaults
        // property set so live slider changes propagate to every face without
        // rebuilding the effect graph.
        var globals = LightingDefaults.GetOrCreate(compositor);

        // Apply per-material overrides or bind to global defaults
        var lp = material.Lighting;
        if (lp?.AmbientAmount is { } a)
            effectBrush.Properties.InsertScalar("Lighting.AmbientAmount", a);
        else
            BindScalar(effectBrush, "Lighting.AmbientAmount", globals, "AmbientAmount");

        if (lp?.DiffuseAmount is { } d)
            effectBrush.Properties.InsertScalar("Lighting.DiffuseAmount", d);
        else
            BindScalar(effectBrush, "Lighting.DiffuseAmount", globals, "DiffuseAmount");

        if (lp?.SpecularAmount is { } sp)
            effectBrush.Properties.InsertScalar("Lighting.SpecularAmount", sp);
        else
            BindScalar(effectBrush, "Lighting.SpecularAmount", globals, "SpecularAmount");

        if (lp?.SpecularShine is { } sh)
            effectBrush.Properties.InsertScalar("Lighting.SpecularShine", sh);
        else
            BindScalar(effectBrush, "Lighting.SpecularShine", globals, "SpecularShine");

        return effectBrush;
    }

    private static void BindScalar(
        CompositionEffectBrush effectBrush, string effectProperty,
        CompositionPropertySet source, string sourceProperty)
    {
        var expr = effectBrush.Compositor.CreateExpressionAnimation(
            $"globals.{sourceProperty}");
        expr.SetReferenceParameter("globals", source);
        effectBrush.StartAnimation(effectProperty, expr);
    }
}
