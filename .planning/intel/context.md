# Intel: Context Notes (DOC sources)

Ingest set: 1 DOC — `style_extraction.md`
Classification: `.planning/intel/classifications/style-extraction-00002268.json`
(type DOC, confidence medium, precedence default lowest, locked false, cross_refs none)

Entries are recorded faithfully from the research note with `source:` attribution. Bracketed
annotations `[gsd:]` map proposals against shipped/locked existing context — they annotate, they
do not modify the doc's claims. Tension resolution lives in `.planning/INGEST-CONFLICTS.md`.

---

## Topic: Reference scene decomposition (proposed front end to Phase 5)

The doc's central proposal: game screenshots have already gone through lighting and
post-processing, so stylization should decompose references before palette extraction:

```text
3–10 rendered reference screenshots
        ↓
 Scene Appearance Analysis
   ├── post-process estimate (exposure, white balance, tone curve,
   │   contrast, saturation, LUT/color shift)
   └── image decomposition (albedo, shading, specular, emissive,
       bloom mask, confidence)
        ↓
 recovered base colors
        ↓
 OKLab style/palette model
        ↓
 existing NAMER vertex-color fitting
        ↓
 NAMER model + Unity Volume/LUT
        ↓
 rendered scene match
```

Proposed output struct:

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

source: .planning/research/style_extraction.md

[gsd:] The shipped 05.1 fit (`NamerStylePostFit`) already estimates the same post-process
parameter family (von Kries WB, EV ratio, percentile contrast, chroma saturation,
histogram-spec curves, bloom percentile) — see INFO entries in INGEST-CONFLICTS.md. The doc's
decomposition stage is a new front end, not a replacement for that fit.

---

## Topic: Palette computed from recovered/de-graded base colors

Quote: "compute the palette from the recovered base colors rather than raw pixels."

Example given:
```text
raw screenshot orange = #E58B48
might really be:
base asset color = #A96742
post grade -> #E58B48
```
"You want `#A96742` in the vertex colors."

source: .planning/research/style_extraction.md

[gsd:] Competes with shipped STYL-02/D-04 raw-reference-pixel palette extraction and raises a
grade-consistency question with the fitted NamerPostLook Volume (de-grade + forward Volume must
be inverse-consistent or the grade is double-applied). See [WARNING] in INGEST-CONFLICTS.md.

---

## Topic: Hue/role-aware vertex-color palette remapping (proposed 05-03)

Quote: "Instead of altering the full-resolution source texture and then decomposing it, we can
often operate directly on the fitted vertex colors":

```text
current vertex colors → convert to OKLab → classify relative hue/lightness role
→ map against recovered style distribution → new Color32[]
```

Mapping by role (dark red → reference dark red, mid red → reference mid red, light red →
reference light red; same for blue, etc.) "rather than nearest-palette-color quantization."
"That should retain the structure NAMER has already extracted."

source: .planning/research/style_extraction.md

[gsd:] Post-decomposition mesh-domain VC editing was explicitly rejected in shipped
05-CONTEXT.md D-01 ("stylize the source, then fit"; user-confirmed). The rejection rationale
(un-stylized residual re-adds original detail) only partially applies in today's D-13
one-texture default (residual is white; VCs are the sole color carrier). User decision required
— see [WARNING] in INGEST-CONFLICTS.md.

---

## Topic: Down-weighting contaminated pixels during palette extraction

Bloom should NOT be recovered as part of the base palette. Instead identify
bloom-contaminated pixels and give them very low or zero weight during palette extraction.
Same for: specular highlights, emissive pixels, very deep shadows, clipping, fog, transparent
effects. "That leaves the most useful pixels for determining what the artist probably put into
the underlying material."

source: .planning/research/style_extraction.md

[gsd:] Additive — no shipped counterpart, no contradicting decision. Complements STYL-08
(bloom threshold fitted from reference highlights).

---

## Topic: Forward scene validation assets (proposed 05-04)

Quote: "Have Namer optionally create:
```text
NAMERStyleProfile.asset
ReferenceStyleVolume.asset
ReferenceStyleLUT.cube / Texture3D
```
Then validate: NAMER vertex colors + NAMER surface texture + Unity lighting + recovered Volume
settings → render → compare with reference style. 'This closes the loop.'"

source: .planning/research/style_extraction.md

[gsd:] Three tensions with shipped context: (1) LUT asset vs the locked 5.1 decision
"parametric named Volume knobs (3D-LUT/ColorLookup is a plan-time fallback only)"
(05.1-CONTEXT.md, "Locked by ROADMAP §Phase 5.1"); (2) asset home — NamerStyleProfile is
palette-only since 05.2 (STYL-10/11/12; post look + Volume live on NamerPostLook); (3) forward
validation itself is additive. See [WARNING] + [INFO] entries in INGEST-CONFLICTS.md.

---

## Topic: Proposed Phase 5 restructure into four plans

The doc proposes:
- 05-01 — Reference Scene Decomposition (analysis: exposure, white balance, tone curve,
  contrast, saturation, shadow/midtone/highlight shifts, probable LUT transform, bloom regions,
  emissive/specular regions, approximate albedo/base-color regions, confidence mask)
- 05-02 — NAMERStyleProfile + Base Palette Extraction (existing Phase 5 work, but from
  recovered base colors)
- 05-03 — Hue/Role-Aware Vertex Palette Remapping
- 05-04 — Forward Scene Validation / Unity Volume ("the part I'd add that isn't in the current
  roadmap")

Quote: "I would insert the inverse-render/reference decomposition before the existing 05-01,
then push the existing plans down. That avoids throwing away any of the work already planned."

source: .planning/research/style_extraction.md

[gsd:] The doc's roadmap snapshot is stale: ROADMAP.md marks Phase 5 complete 3/3 (2026-09-24),
Phase 5.1 complete 4/4 (2026-09-25), Phase 5.2 complete 6/6 (2026-09-28). The "push down"
cannot apply to executed plans — see [WARNING] in INGEST-CONFLICTS.md.

---

## Topic: Conceptual framing (extension of the NAMER idea)

NAMER already separates geometry / surface response / base color and encodes base color in
vertices. The doc extends the same idea outward: final game screenshot → separate scene
post-processing → separate lighting → estimate what belonged to the asset → use that to style
another NAMER asset. Also positions the NAMER paper's later section as "reference-driven
appearance transfer that distinguishes asset color from scene rendering" rather than plain
palette matching.

source: .planning/research/style_extraction.md

[gsd:] Consistent with shipped STYL-11 philosophy (palette never touches luma; post owns
tonality).
