# Combobulate — usage guide

This document covers everything beyond the minimal "drop a control, point
`Source` at a file" path shown in the [README](../README.md). Read top to
bottom or jump to the section you need.

- [Loading models](#loading-models)
  - [From an asset URI](#from-an-asset-uri)
  - [From an absolute file path](#from-an-absolute-file-path)
  - [From a parsed `ObjModel`](#from-a-parsed-objmodel)
  - [From a stream or arbitrary text](#from-a-stream-or-arbitrary-text)
  - [Sharing one model across many controls](#sharing-one-model-across-many-controls)
- [Materials and textures](#materials-and-textures)
  - [Auto-loading via `mtllib`](#auto-loading-via-mtllib)
  - [Custom material packs](#custom-material-packs)
  - [Texture sources](#texture-sources)
  - [Live-updating pixels](#live-updating-pixels)
  - [`MaterialMode`](#materialmode)
- [Control properties](#control-properties)
- [Supported `.obj` subset](#supported-obj-subset)
- [Supported `.mtl` subset](#supported-mtl-subset)
- [The cache, in one minute](#the-cache-in-one-minute)
- [Diagnostics and parse errors](#diagnostics-and-parse-errors)

## Loading models

The control accepts geometry in three forms, in priority order: `Source` →
`Model` → (nothing). Pick whichever fits how your app already manages files.

### From an asset URI

Mark the file as `Content` (`CopyToOutputDirectory=PreserveNewest`) and
reference it via `ms-appx:///`:

```xml
<c:Combobulate Source="ms-appx:///Assets/book.obj" />
```

The first control to reference that URI parses + caches the model; every
subsequent control reuses the cached `ObjGeometry`.

### From an absolute file path

```xml
<c:Combobulate x:Name="Viewer" />
```

```csharp
Viewer.Source = @"C:\models\book.obj";
```

`Source` records the file's directory so any `mtllib` inside the OBJ resolves
relative to it.

### From a parsed `ObjModel`

If you already have the file contents in memory, parse it yourself and
assign `Model` directly:

```csharp
using Combobulate.Parsing;

var result = ObjParser.Parse(text);
Viewer.Model = result.Model;          // partial model on parse error
foreach (var err in result.Errors)
    Debug.WriteLine(err);
```

Setting `Model` directly bypasses the keyed source cache (no `mtllib`
auto-load), but the per-instance geometry cache still kicks in — reusing the
same `ObjModel` reference across many controls only does the per-quad
model-space prep once.

### From a stream or arbitrary text

Read the bytes however you like, hand the text to `ObjParser.Parse`, then
assign `Model`:

```csharp
using var stream = await someFile.OpenStreamForReadAsync();
using var reader = new StreamReader(stream);
var result = ObjParser.Parse(await reader.ReadToEndAsync());
Viewer.Model = result.Model;
```

If you need `mtllib` resolution against a base directory you control, use
`ObjCache.GetOrLoadMtlForModel(model, baseDirectory)` and assign the
returned pack to `Materials` (see below).

### Sharing one model across many controls

Two patterns work:

1. **By `Source`** — every control with the same string value reuses the
   cached `ObjGeometry` automatically.
2. **By cache key** — register a model under a logical name and reference
   it by key:

   ```csharp
   ObjCache.Register("book", parsedModel);
   ```

   ```xml
   <c:Combobulate Source="book" />
   ```

   The lookup checks registered keys before treating the value as a path.

## Materials and textures

A face's appearance is determined by the active *material pack* — a
dictionary keyed by the OBJ's `usemtl` names. The control resolves a pack at
rebuild time in this order:

1. `Materials` dependency property (your custom pack), if non-null.
2. A pack previously registered against `Source` via
   `ObjCache.RegisterMaterials(sourceKey, ...)`.
3. Materials auto-loaded from any `mtllib` declarations in the OBJ.
4. The per-quad fallback colour baked into the geometry from the OBJ palette.

### Auto-loading via `mtllib`

If your OBJ contains `mtllib book.mtl` and you set `Source` to a file path
or `ms-appx:///` URI, the control finds `book.mtl` next to the OBJ, parses
it, resolves `map_Kd` paths relative to the same directory, and applies the
resulting pack — no code required.

### Custom material packs

Build a pack programmatically when you want full control over which faces
get which texture or colour:

```csharp
using Combobulate.Caching;
using Windows.UI;

var pack = new ObjMaterialPackBuilder()
    .Add("cover", new ObjMaterial
    {
        Name           = "cover",
        DiffuseTexture = ObjTextureSource.FromUri(new Uri("ms-appx:///Assets/cover.png")),
        UvScale        = new Vector2(1, 1),
        UvOffset       = new Vector2(0, 0),
        ClampUv        = true,
    })
    .Add("pages", new ObjMaterial
    {
        Name           = "pages",
        DiffuseTexture = ObjTextureSource.FromFile(@"C:\textures\pages.png"),
    })
    .Add("spine", new ObjMaterial
    {
        Name         = "spine",
        DiffuseColor = Color.FromArgb(255, 80, 30, 20),
    })
    .Build();

Viewer.Source    = "ms-appx:///Assets/book.obj";
Viewer.Materials = pack;     // overrides any mtllib in the OBJ
```

Pack keys are matched against the OBJ's `usemtl` names case-sensitively
(ordinal). Faces whose `usemtl` is missing from the pack fall through to the
pack's `Fallback` (set via `ObjMaterialPackBuilder.Fallback`) or, failing
that, the per-quad palette colour.

If you don't author the OBJ yourself, inspect the geometry to discover the
material names:

```csharp
var geometry = ObjCache.GetOrLoadGeometry(path);
foreach (var quad in geometry.Quads)
    Debug.WriteLine(quad.MaterialName);
```

### Texture sources

`ObjTextureSource` is the texture identity. Sources are cached process-wide
by `CacheKey`, so the same image is decoded once and the resulting surface
is reused across every control that brushes with it.

| Factory | Backed by | Cache key | Updateable |
| --- | --- | --- | --- |
| `FromUri(Uri)` | `LoadedImageSurface.StartLoadFromUri` | `uri:<absolute>` | no |
| `FromFile(string)` | absolute path → `LoadedImageSurface` | `uri:<absolute>` | no |
| `FromStream(factory, key)` | `LoadedImageSurface.StartLoadFromStream` | `stream:<key>` | yes |
| `FromBitmap(SoftwareBitmap)` | PNG-encoded bytes | `bmp:<n>` | yes |
| `FromSurface(ICompositionSurface)` | your own surface (Win2D, etc.) | `ext:<n>` | no |

### Live-updating pixels

Sources made with `FromBitmap` or `FromStream` accept
`Update(SoftwareBitmap)`. The control re-points the underlying
`CompositionSurfaceBrush.Surface` without rebuilding geometry, so animation
is flicker-free:

```csharp
var paintTex = ObjTextureSource.FromBitmap(MakeBitmap(0));   // 128×128 BGRA8 premultiplied
var pack     = new ObjMaterialPackBuilder()
                  .Add("front", new ObjMaterial { DiffuseTexture = paintTex })
                  .Build();
Viewer.Materials = pack;

// Later, on a tick:
paintTex.Update(MakeBitmap(tick++));
```

`FromUri`, `FromFile`, and `FromSurface` are read-only.

### `MaterialMode`

Controls how the active pack interacts with OBJ material data:

| Value | Behaviour |
| --- | --- |
| `Auto` (default) | Use textures + diffuse colours from the pack. |
| `UseDiffuse` | Strip textures from the pack; only the diffuse colour applies. |
| `UseFallback` | Ignore the pack entirely; use the per-quad palette colour from the OBJ. |

`UseFallback` is the "show me the OBJ as if no materials existed" mode.

## Control properties

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `Source` | `string?` | `null` | File path, `ms-appx:///` URI, or registered `ObjCache` key. |
| `Model` | `ObjModel?` | `null` | Direct model assignment. Bypasses keyed cache and `mtllib` auto-load. |
| `Materials` | `ObjMaterialPack?` | `null` | Overrides any auto-resolved material pack. |
| `MaterialMode` | `MaterialMode` | `Auto` | See above. |
| `ModelScale` | `double` | `100` | Pixels per model unit. |
| `EnablePerspective` | `bool` | `true` | Applies a perspective `M34` to the host transform. |
| `RotationX` / `RotationY` / `RotationZ` | `double` | `0` | Euler angles in degrees. |

The control rebuilds its visual tree whenever any of these change.

## Supported `.obj` subset

- `v x y z` — vertex positions
- `vt u v [w]` — texture coordinates (used when materials supply textures)
- `f a b c d` — **quad** faces (4 indices, 1-based, positive or negative)
- `f a/ta b/tb c/tc d/td` — quads with texture indices
- `usemtl <name>` — selects the active material for subsequent faces
- `mtllib <file> [<file>…]` — relative paths resolved against the OBJ
- Vertex normals in `f` entries (`a/ta/na`) are accepted but ignored

Faces must be **quads** — triangles and n-gons are reported as parse errors.
Wind each face so `(V1 - V0) × (V3 - V0)` points outward; that's the
direction the renderer treats as front-facing for back-face culling.

See [`samples/book.obj`](../samples/book.obj) for a worked example.

## Supported `.mtl` subset

- `newmtl <name>` — start a material definition
- `Kd r g b` — diffuse colour (linear RGB 0..1)
- `Ka`, `Ks`, `Ke`, `Ns`, `Ni`, `d`, `Tr`, `illum` — parsed and stored, but
  only diffuse is used for rendering today
- `map_Kd <path>` — diffuse texture; relative paths resolve against the
  `.mtl` file's directory
- `map_Ka`, `map_Ks`, `map_d`, `map_Bump` / `bump` / `map_Kn` — parsed but
  not yet rendered
- Texture options on a `map_*` line: `-s u v`, `-o u v`, `-clamp on|off`,
  `-mm base gain`; unknown options are preserved in `ExtraOptions`
- Unknown keywords are preserved in `ExtraKeywords` rather than failing the
  parse — the parser is lenient by design

## The cache, in one minute

`Combobulate.Caching.ObjCache` is process-wide and split into a few keyed
stores:

- **Source models** — `Source` values map to parsed `ObjModel` + per-instance
  `ObjGeometry`. Files are invalidated by timestamp + length.
- **Material packs** — registered explicitly via
  `RegisterMaterials(sourceKey, pack)` or auto-loaded via
  `GetOrLoadMtlForModel(model, baseDirectory)`. Same timestamp/length
  invalidation as models.
- **Texture surfaces** — keyed by `ObjTextureSource.CacheKey`; one
  `ICompositionSurface` per identity, shared across every brush.
- **Brush bindings** — per-(geometry, pack) `CompositionSurfaceBrush`
  instances are kept in `ConditionalWeakTable`s so they're collected when
  the geometry or pack is.

Useful APIs:

```csharp
ObjCache.Register(key, model);                         // logical alias
ObjCache.GetOrAdd(key, () => parseModelOnce());        // lazy register
ObjCache.RegisterMaterials(sourceKey, pack);
ObjCache.TryGetMaterials(sourceKey, out var pack);
ObjCache.GetOrLoadMtlFile(absolutePath, baseDir);
ObjCache.GetOrLoadMtlForModel(model, baseDir, onError);
ObjCache.InvalidateMaterials(sourceKey);
ObjCache.InvalidateMtlFile(absolutePath);
```

## Diagnostics and parse errors

`ObjParser.Parse` and `MtlParser.Parse` return a result type containing both
the (possibly partial) model and a list of structured errors. Errors never
throw — they accumulate so you can show them in your UI or log them:

```csharp
var result = ObjParser.Parse(text);
foreach (var err in result.Errors)
    Debug.WriteLine($"{err.Line}: {err.Kind} — {err.Message}");
Viewer.Model = result.Model;     // still useful even with errors
```

For `mtllib` auto-load errors, pass an `onError` callback to
`ObjCache.GetOrLoadMtlForModel`.
