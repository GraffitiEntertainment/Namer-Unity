# Research Questions

Open questions that need deeper investigation before or during planning. Append with date + origin.

---

## 2026-09-16 — Overlapping-UV texels in the decomposition rasterizer (tripo/AI meshes)

**Origin:** /gsd-explore roughness-redesign session (`.planning/notes/sobel-led-residual-guided-roughness.md`).

The decomposition rasterizer resolves per-texel vertex-color interpolation
first-covering-triangle-wins (`Compute/NAMERDecomp.compute:111-113`). On meshes with
mirrored or overlapping UVs (common in tripo/AI-generated assets like Neo), one triangle's
interpolated color lands on another triangle's texels — a guaranteed per-texel mismatch in
the residual quotient that no smoothing or Gouraud projection can fix.

Questions to answer:

- How prevalent is this on real target assets (Neo first: do the non-white residual spots
  correlate with UV-overlap regions)?
- Candidate strategies and their costs: detect-and-report (mark overlapping texels,
  exclude from FitOnlyMaxError), last-wins vs. highest-UV-area-triangle-wins, per-island
  re-rasterization, or import-time UV overlap repair (xatlas-style).
- Does the 6-bit packed surface roughness quantization interact with the residual's 8-bit
  vcInterp quantization in a way that bounds achievable whiteness?

**Blocks:** the projection design's "residual white by construction" claim — overlap
texels are the one known exception class.
