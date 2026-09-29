## Conflict Detection Report

Ingest: 1 DOC (.planning/research/style_extraction.md, classification
style-extraction-00002268.json — DOC / medium confidence / not locked / no cross-refs)
Mode: merge. Existing context consulted: .planning/PROJECT.md, .planning/REQUIREMENTS.md,
.planning/ROADMAP.md, plus phase decision artifacts under .planning/phases/ (05-CONTEXT.md,
05.1-CONTEXT.md, 05.2-CONTEXT.md — these DO exist; the "no phase CONTEXT.md files" note in the
ingest request was inaccurate).

Cycle detection: no cross-references in the ingest set; graph is a single node; no cycles;
traversal depth 1 (cap 50). No UNKNOWN/low-confidence classifications.

### BLOCKERS (0)

None. No locked ADRs in the ingest set, no LOCKED-vs-LOCKED contradiction, and the ingest
document is itself unlocked (research note at lowest precedence), so existing locked decisions
prevail by precedence rather than blocking.

### WARNINGS (4)

[WARNING] Proposed Phase 5 restructure assumes unexecuted plans
  Found: .planning/research/style_extraction.md proposes inserting "05-01 Reference Scene Decomposition" before the existing 05-01 and pushing existing plans down ("I would insert the inverse-render/reference decomposition before the existing 05-01, then push the existing plans down"), quoting the Phase 5 plan list (05-01 NAMERStyleProfile + palette extraction, 05-02 hue-aware mapping + smoothing, 05-03 AO/normal/roughness controls) as current/future work
  Expected: .planning/ROADMAP.md records Phase 5 complete 3/3 (2026-09-24), Phase 5.1 complete 4/4 (2026-09-25, INSERTED), Phase 5.2 complete 6/6 (2026-09-28, INSERTED); those plan IDs are executed history with summaries under .planning/phases/05-stylization/, .../05.1-.../, .../05.2-.../, and REQUIREMENTS.md traceability maps STYL-01..12 onto them
  Impact: Renumbering or reordering executed plans rewrites validated phase history and breaks requirement traceability (incl. the 314/314 test baseline recorded in PROJECT.md); the proposal cannot be merged as written
  → Re-scope the decomposition work as a NEW inserted phase (e.g. 05.3 or 6) layered onto the shipped 05/05.1/05.2 pipeline; do not push executed plans down

[WARNING] Competing palette-source variants for STYL-02 (de-graded base colors vs raw reference pixels)
  Found: .planning/research/style_extraction.md (proposed 05-02) would compute the palette "from the recovered base colors rather than raw pixels" — e.g. raw screenshot orange #E58B48 is really base asset color #A96742 after the grade, and #A96742 is what belongs in vertex colors
  Expected: STYL-02 (Complete, .planning/REQUIREMENTS.md) ships palette/style-stat extraction from the reference images as sampled; .planning/phases/05-stylization/05-CONTEXT.md D-04 fixes the mechanism (all 3–10 references sampled at capped resolution into ONE merged aggregate palette/stat set, deterministic CPU Burst extraction)
  Impact: Changing the palette source alters validated STYL-02/STYL-03 behavior and its test pins; it also creates a grade-consistency obligation — de-grading references before palette extraction while the fitted NamerPostLook Volume (05.1/05.2) applies the grade at render time must be inverse-consistent, or the grade is double-applied
  → User must pick one variant before routing: keep the shipped raw-pixel palette, add a de-graded mode behind a switch, or replace; if de-graded is chosen, pin inverse-consistency with the post-look fit as acceptance criteria

[WARNING] Post-fit vertex-color remapping contradicts shipped D-01 pipeline position
  Found: .planning/research/style_extraction.md (proposed 05-03) would operate "directly on the fitted vertex colors" (current VCs → OKLab → hue/lightness role classification → map against recovered style distribution → new Color32[]) "rather than nearest-palette-color quantization"
  Expected: .planning/phases/05-stylization/05-CONTEXT.md D-01 (<decisions> block, user-confirmed): stylization runs in UV space BEFORE the VC fit/projection ("stylize the source, then fit"); "Post-decomposition mesh-domain VC editing was rejected" (edge-preserving smoothing is atlas-space only; an un-stylized residual would re-add the original detail)
  Impact: Reopens an explicitly rejected design. Note the D-01 rejection rationale only partially applies today: in the D-13 one-texture default the residual is white (VCs are the sole color carrier), so post-fit VC remapping is not automatically blocked — but any OKLab role mapping must still satisfy STYL-03 (hue identity preserved) and STYL-11 (palette never transfers luma), and the shipped smoothing path (STYL-04, atlas-space) does not run on VCs
  → User decision: uphold D-01 (record the doc's variant as a rejected alternative) or supersede D-01 with a new decision explicitly covering post-fit VC remapping; either way keep hue-preservation and no-luma-transfer acceptance pinned

[WARNING] Post-look asset ownership contradicts shipped 05.2 standalone NamerPostLook architecture
  Found: .planning/research/style_extraction.md (proposed 05-04) lists the new artifacts as "NAMERStyleProfile.asset, ReferenceStyleVolume.asset, ReferenceStyleLUT.cube / Texture3D" — Volume/LUT artifacts coupled to the style profile
  Expected: STYL-10/11/12 (Complete, .planning/REQUIREMENTS.md; decisions in .planning/phases/05.2-.../05.2-CONTEXT.md): NamerStyleProfile is palette-only; the reference-fitted post look, its own reference list, and per-asset URP Volume generation live on the standalone NamerPostLook ScriptableObject; ToneMatch retired so tonality is owned exclusively by the post look
  Impact: Routing a recovered Volume/LUT through the style profile would undo the 05.2 asset split and its executed live migration; synthesis cannot pick an asset home for the proposed ReferenceStyleAnalysis / ReferenceStyleVolume / ReferenceStyleLUT outputs without losing intent
  → Decide the asset home for the decomposition outputs (extend NamerPostLook vs a new reference-analysis asset type); NamerStyleProfile stays palette-only per STYL-10

### INFO (4)

[INFO] Auto-resolved: locked 5.1 parametric-knobs decision over the doc's LUT proposal
  Note: .planning/research/style_extraction.md proposes estimating a "probable LUT transform" and creating a first-class ReferenceStyleLUT.cube / Texture3D output asset. .planning/phases/05.1-reference-fitted-urp-post-processing-look-inserted/05.1-CONTEXT.md locks the opposite: "parametric named Volume knobs (3D-LUT/ColorLookup is a plan-time fallback only)", under a block marked "Locked by ROADMAP §Phase 5.1 (success criteria 1–5), not re-discussed" (ROADMAP.md Phase 5.1 criterion 3: named, tweakable Volume parameters — not an opaque LUT). The ingest doc is a non-locked DOC at lowest precedence, so the locked decision wins: the LUT route stays a plan-time fallback, conditionable only on visible residual mismatch or an explicit supersession. The doc's LUT-estimation idea is preserved in intel (context.md, "Reference scene decomposition") as an analysis-side output (ReferenceStyleAnalysis.colorTransform), not as the output representation.

[INFO] Auto-resolved: post-process estimation overlaps the shipped NamerStylePostFit family — reuse, do not re-derive
  Note: The doc's post-process estimate (exposure, white balance, tone curve, contrast, saturation, LUT/color shift) covers the same parameter family already fitted deterministically by NamerStylePostFit in Phase 5.1 (von Kries WB, mean-luma EV ratio, percentile contrast, perceptual chroma saturation, histogram-spec curves, bloom percentile — locked by ROADMAP 5.1 criterion 1; CPU editor-side, no new package dependencies, test-pinned). No contradiction: the doc estimates the reference's grade where 5.1 fits source-to-reference. Any adopted decomposition stage should consume/invert the existing fit math rather than introduce a second estimator family.

[INFO] Alignment: decomposition philosophy consistent with shipped palette/post split
  Note: The doc's separation of scene post-processing / lighting / asset color is consistent with STYL-11 (palette transfer never modifies luma; tonality owned exclusively by the post look) and with the 05.2 standalone-post-look architecture. Its additive proposals — bloom/specular/emissive/deep-shadow/fog/transparent pixel down-weighting during palette extraction, confidence masking, and forward Volume validation against the reference — have no shipped counterpart and no contradicting decision; recorded in intel as candidates (context.md topics "Down-weighting contaminated pixels" and "Forward scene validation assets").

[INFO] Existing-context inconsistencies surfaced during merge (not caused by the ingest)
  Note: .planning/REQUIREMENTS.md marks STYL-07/08/09 unchecked "Not Started" (Phase 5.1) while .planning/ROADMAP.md shows Phase 5.1 complete 4/4 (2026-09-25) and .planning/PROJECT.md records STYL-07/08/09 "delivered across Phases 05/05.1/05.2 at code level; checkbox close-out awaits the human visual confirmations parked in 05.1-VERIFICATION". The ROADMAP.md progress table also lacks rows for phases 04.2, 04.3, and 05.2 although their phase-detail sections exist. The roadmapper should reconcile checkbox/table state before routing, and note that phase CONTEXT.md files DO exist (the ingest request's "no phase CONTEXT.md files exist" was inaccurate) — their decision blocks were consulted for this report.
