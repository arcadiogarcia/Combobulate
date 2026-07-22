# Migrating off the built-in lit path → host-supplied effect graphs

The built-in lighting path — `ObjMaterial.NormalMap`, `ObjMaterial.Lighting`,
`LightingDefaults`, and `LightingParams` — is **deprecated**. Combobulate is
per-face brush *plumbing*; the *look* (including lighting) belongs to the host
app. The general-purpose `MaterialSlotController.SetEffect(...)` API can express
everything the built-in lit path did, plus arbitrary composites, and it works on
**both** the lifted (`Microsoft.UI.Composition`) and system
(`Windows.UI.Composition`) compositors.

This document shows how to reproduce the old lit look from your own app.

## What the built-in path did

For a material with a `NormalMap`, the resolver wrapped the diffuse brush in:

```
ArithmeticCompositeEffect(
    MultiplyAmount = 1, Source1Amount = 0, Source2Amount = 0, Offset = 0,
    ClampOutput = true)
{
    Source1 = <diffuse>,                    // CompositionEffectSourceParameter("Diffuse")
    Source2 = SceneLightingEffect {         // CompositionEffectSourceParameter("NormalMap")
        NormalMapSource = <normal>,
        AmbientAmount, DiffuseAmount, SpecularAmount, SpecularShine
    }
}
```

…and live-bound the four scalars to a process-wide `LightingDefaults` property
set (defaults `0.4 / 1.0 / 0.2 / 16`), with optional per-material overrides via
`LightingParams`.

> **Why `ArithmeticCompositeEffect` and not `BlendEffect(Multiply)`?** D2D's
> `BlendEffect` uses source-over-with-blend math; anti-aliased sprite edges
> (`src.a < 1`) leak the bright lighting term and produce a flickery cream/white
> rim. `ArithmeticCompositeEffect` does a pure premultiplied componentwise
> multiply (`Source1Amount = 0, Source2Amount = 0, MultiplyAmount = 1, Offset =
> 0, ClampOutput = true`) — no rim. **Reproduce this exactly** in your host
> graph.

## Host recipe (lifted compositor — tray/mat)

You need Win2D (`Microsoft.Graphics.Win2D`) in the host to author the graph
description. The graph is just a description; Combobulate instantiates it.

```csharp
using Microsoft.Graphics.Canvas.Effects; // Win2D effect descriptions
using Windows.Graphics.Effects;
using Microsoft.UI.Composition;

// 1) Your diffuse sub-graph. It can be a single atlas OR an arbitrary composite
//    (e.g. Pip's ScreenProjected wallpaper + PerFaceUv numerals).
IGraphicsEffect diffuse = new CompositeEffect
{
    Mode = CanvasComposite.SourceOver,
    Sources =
    {
        new CompositionEffectSourceParameter("base"),
        new CompositionEffectSourceParameter("numerals"),
    },
};

// 2) Wrap it in the lit graph — same math as the old built-in path.
var lit = new ArithmeticCompositeEffect
{
    MultiplyAmount = 1f,
    Source1Amount  = 0f,
    Source2Amount  = 0f,
    Offset         = 0f,
    ClampOutput    = true,
    Source1 = diffuse,
    Source2 = new SceneLightingEffect
    {
        Name            = "lighting",           // name it to bind scalars live
        NormalMapSource = new CompositionEffectSourceParameter("normal"),
    },
};

// 3) A host-owned property set for the four scalars (replaces LightingDefaults).
var lightScalars = compositor.CreatePropertySet();
lightScalars.InsertScalar("AmbientAmount",  0.4f);
lightScalars.InsertScalar("DiffuseAmount",  1.0f);
lightScalars.InsertScalar("SpecularAmount", 0.2f);
lightScalars.InsertScalar("SpecularShine",  16f);

// 4) Map the named sources. A flat normal is just a solid (128,128,255) colour —
//    no surface/decode required (MaterialLayer.Color), works on both compositors.
var sources = new Dictionary<string, MaterialLayer>
{
    ["base"]     = MaterialLayer.ScreenProjected(wallpaperSurface, projSet, "SS"),
    ["numerals"] = MaterialLayer.PerFaceUv(numeralAtlas),
    ["normal"]   = MaterialLayer.Color(Color.FromArgb(255, 128, 128, 255)),
};

// 5) Apply. boundProperties live-binds the scalars across every face at once.
slots.SetEffect(
    "numbered",
    lit,
    sources,
    sharedProperties: lightScalars,
    boundProperties: new[]
    {
        "lighting.AmbientAmount",
        "lighting.DiffuseAmount",
        "lighting.SpecularAmount",
        "lighting.SpecularShine",
    });
```

### The light rig

`SceneLightingEffect` samples the composition lights on the visual tree. Add your
lights to Combobulate's `RootVisual` directly — XAML `AddTargetElement` does **not**
cascade to the internal hand-off visual:

```csharp
var distant = compositor.CreateDistantLight();
distant.CoordinateSpace = windowRootVisual;
distant.Direction = new Vector3(0.342f, 0.174f, -0.940f); // 20° left, 10° up
distant.Color = Colors.White;
distant.Targets.Add(combobulate.RootVisual);   // <-- the die's hand-off visual
```

Because the light is fixed in screen space and the face sprites carry a flat
normal, each face dims as it yaws away — exactly like the old path.

## Ghost / system compositor

The ghost die runs on `Windows.UI.Composition` (no Win2D in that library). The
host still authors the **same Win2D description** and hands it to the system
compositor — `Combobulate.SystemComp.SetEffect` calls `CreateEffectFactory` with
your `IGraphicsEffect`, which pulls in no Win2D runtime. Differences:

- Use `Windows.UI.Composition.Effects.SceneLightingEffect` (system flavour). The
  Win2D `ArithmeticCompositeEffect` / `CompositeEffect` descriptions work for both
  compositors.
- Create the lights on the **system** compositor and add them to the ghost's
  `RootVisual`; lights are per-compositor.
- `MaterialLayer.Color(...)` for the flat normal works unchanged (no surface
  decode, so no `FromBitmap`/Win2D dependency on that compositor).

## Per-material overrides

The old `LightingParams` per-material override becomes: either bake the constant
into that material's graph, or give that material its own `SharedEffectProperties`
set with different scalar values. Combobulate no longer owns any lighting state.

## Removal timeline

The deprecated members remain **functional** during the deprecation window so
existing consumers keep building and rendering. Migrate to `SetEffect`, then the
lit path (`BuildLitBrush`, `GetOrCreateLitFactory`, the `NormalMap`/`Lighting`
branches, `LightingDefaults`, `LightingParams`) will be removed in a later
release.
