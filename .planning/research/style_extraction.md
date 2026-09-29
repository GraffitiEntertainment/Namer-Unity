Exactly. I checked the current repo, and **Namer-Unity is already the right home for this**. I was treating the idea too much like a separate project.

The current implementation is already most of the way toward the pipeline we just described:

- Phase 03.1 already extracts AO and un-multiplies baked AO from base color.
- Phase 4 already fits base color into **vertex colors** and computes residuals.
- Phase 04.1/04.2 already pull baked surface response out of color and move it into roughness while targeting the one-texture path.
- The PRD already specifies **3–10 reference images** for style analysis.
- Phase 5 already calls for palette, saturation, luminance, shadows/midtones/highlights, warm/cool bias, contrast, hue-aware mapping, smoothing, AO, normals, and roughness.

So what we just identified is really a missing **front end to Phase 5**.

### The key change I would make

Right now Phase 5 effectively says:

```text
reference screenshots
        ↓
extract colors/style
        ↓
map asset colors
```

But game screenshots have already gone through lighting and post-processing. We should instead make it:

```text
3–10 rendered reference screenshots
                ↓
     Scene Appearance Analysis
                │
     ┌──────────┴───────────┐
     │                      │
post-process estimate    image decomposition
     │                      │
 exposure                   albedo
 white balance              shading
 tone curve                 specular
 contrast                   emissive
 saturation                 bloom mask
 LUT/color shift            confidence
     │                      │
     └──────────┬───────────┘
                ↓
       recovered base colors
                ↓
     OKLab style/palette model
                ↓
 existing NAMER vertex-color fitting
                ↓
         NAMER model
                +
        Unity Volume/LUT
                ↓
      rendered scene match
```

That is a much better extension of what NAMER already does.

## I would change Phase 5 into four plans

**05-01 — Reference Scene Decomposition**

Analyze the 3–10 images for:

- exposure
- white balance
- tone curve
- contrast
- saturation
- shadow/midtone/highlight shifts
- probable LUT transform
- bloom regions
- emissive/specular regions
- approximate albedo/base-color regions
- confidence mask

The important output isn't just a palette. It is:

```text
ReferenceStyleAnalysis
{
    baseColorSamples
    confidence[]
    toneModel
    colorTransform
    bloomModel
    luminanceDistribution
}
```

**05-02 — NAMERStyleProfile + Base Palette Extraction**

Then do the thing the current Phase 5 already proposes, except compute the palette from the recovered base colors rather than raw pixels.

For example:

```text
raw screenshot orange = #E58B48
```

might really be:

```text
base asset color = #A96742
post grade -> #E58B48
```

You want `#A96742` in the vertex colors.

That's the important distinction.

**05-03 — Hue/Role-Aware Vertex Palette Remapping**

This dovetails almost perfectly with the current vertex-color work.

Instead of altering the full-resolution source texture and then decomposing it, we can often operate directly on the fitted vertex colors:

```text
current vertex colors
        ↓
convert to OKLab
        ↓
classify relative hue/lightness role
        ↓
map against recovered style distribution
        ↓
new Color32[]
```

So:

```text
dark red   → reference dark red
mid red    → reference mid red
light red  → reference light red

dark blue  → reference dark blue
...
```

rather than nearest-palette-color quantization.

That should retain the structure NAMER has already extracted.

**05-04 — Forward Scene Validation / Unity Volume**

This is the part I'd add that isn't in the current roadmap.

Have Namer optionally create:

```text
NAMERStyleProfile.asset
ReferenceStyleVolume.asset
ReferenceStyleLUT.cube / Texture3D
```

Then validate:

```text
NAMER vertex colors
      +
NAMER surface texture
      +
Unity lighting
      +
recovered Volume settings
      ↓
render
      ↓
compare with reference style
```

This closes the loop.

### Bloom should remain separate

I would **not** try to recover bloom as part of the base palette. Instead Namer should identify bloom-contaminated pixels and give them very low or zero weight during palette extraction.

Same for:

- specular highlights
- emissive pixels
- very deep shadows
- clipping
- fog
- transparent effects

That leaves the most useful pixels for determining what the artist probably put into the underlying material.

### This actually strengthens the original NAMER idea

Namer already takes:

```text
final PBR asset
→ separate what belongs to geometry
→ separate what belongs to surface response
→ separate what belongs to base color
→ encode base color in vertices where possible
```

Phase 5 can extend the same idea one step outward:

```text
final game screenshot
→ separate what belongs to scene post-processing
→ separate what belongs to lighting
→ estimate what belonged to the asset
→ use that to style another NAMER asset
```

So conceptually it's very consistent.

The current roadmap has Phase 5 as:

> `05-01: NAMERStyleProfile + palette/style-stat extraction`  
> `05-02: Hue-aware palette mapping + edge-preserving smoothing compute kernels`  
> `05-03: AO/normal/roughness controls + stylization pipeline integration`

I would **insert the inverse-render/reference decomposition before the existing 05-01**, then push the existing plans down. That avoids throwing away any of the work already planned.

Also, this gives the Namer paper a stronger later section: not merely **texture compression/decomposition**, but **reference-driven appearance transfer that distinguishes asset color from scene rendering**. That's substantially more interesting than plain palette matching.