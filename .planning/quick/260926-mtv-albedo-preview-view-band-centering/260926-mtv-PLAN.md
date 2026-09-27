---
phase: quick-260926-mtv
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  - Packages/com.graffitientertainment.namer/Shaders/NamerChannelView.shader
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs
autonomous: true
requirements: [UI-04]
must_haves:
  truths:
    - "The 512px albedo preview quad centers on the VISIBLE panel band (scroll.x .. scroll.x + viewWidth), not the scroll content width — the old bug centered on the content width set by the widest fixed row (the channel panes)"
    - "The quad never overdraws the horizontal scrollbar or the action header: it is clipped to the visible band and the clipped-off region is removed via the _UvRect sub-rect window (the visible portion is NOT stretched to fill the clipped rect)"
    - "A preview row fully scrolled out of the visible band draws nothing (degenerate drawRect width/height <= 0 skips the draw); the first frame before _scrollViewRect is captured also draws nothing"
    - "Channel panes and the channel popup render byte-identical to before: _UvRect defaults to identity (0,0,1,1), is never set outside DrawAlbedoPreview, and is reset to identity immediately after the preview draw"
    - "All 4 new ComputeAlbedoPreviewGeometry tests plus the 8 existing tests in NamerEditorWindowSmokeTests pass in the orchestrator's live-editor EditMode run"
  artifacts:
    - path: "Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs"
      provides: "View-band centering + band clipping for the albedo preview: scroll-view rect/content-height capture, UvRectId property ID, view-band draw block, pure geometry helper"
      contains: "ComputeAlbedoPreviewGeometry"
    - path: "Packages/com.graffitientertainment.namer/Shaders/NamerChannelView.shader"
      provides: "Identity-default _UvRect sub-rect window applied in ChannelVert"
      contains: "_UvRect"
    - path: "Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs"
      provides: "4 headless geometry tests against the internal helper"
      contains: "CentersInViewWidth_NotRowWidth"
  key_links:
    - from: "OnGUI (captures _scrollViewRect after EndScrollView)"
      to: "DrawAlbedoPreview view-band block"
      via: "instance fields consumed on the next Repaint; content-space rows + widget-space scroll rect"
      pattern: "_scrollViewRect"
    - from: "DrawAlbedoPreview Repaint block"
      to: "NamerChannelView _UvRect"
      via: "SetVector(UvRectId, uvRect) -> Graphics.DrawTexture -> immediate SetVector reset to (0,0,1,1)"
      pattern: "UvRectId"
    - from: "ChannelVert (NamerChannelView.shader)"
      to: "_UvRect"
      via: "output.uv = input.uv * _UvRect.zw + _UvRect.xy (identity default = raw UVs)"
      pattern: "_UvRect\\.zw"
    - from: "NamerEditorWindowSmokeTests (4 new tests)"
      to: "internal static NamerEditorWindow.ComputeAlbedoPreviewGeometry"
      via: "direct call, InternalsVisibleTo (Editor/AssemblyInfo.cs line 5, already present)"
      pattern: "ComputeAlbedoPreviewGeometry"
---

<objective>
Albedo preview view-band centering + explicit scroll-band clipping in NamerEditorWindow, plus the matching shader sub-rect window and headless geometry tests.

Root cause (probe-verified in the live editor 2026-09-26): DrawAlbedoPreview's reserved 512px row uses GUILayout.ExpandWidth(true), which inside a scroll view stretches to max(contentWidth, viewWidth - 13) — i.e. the row rect is pinned to the scroll CONTENT width (set by the widest fixed row, the channel panes), not the visible panel width. The old draw centered on rect.width, so the quad "centers on a fixed width" and, because Graphics.DrawTexture does NOT clip to the scroll view, overdraws both the horizontal scrollbar and the action header below the scroll view. Probe facts that constrain the fix: GUILayout rects inside the scroll view are CONTENT-space coordinates (visible band = [scroll.x, scroll.x + viewWidth] x [scroll.y, scroll.y + viewHeight]); GUILayoutUtility.GetLastRect() immediately after EndScrollView() returns the scroll-view WIDGET rect; an expand row pins at exactly viewWidth when content fits and contentWidth when it overflows (exact horizontal-scrollbar detector); vertical/horizontal scrollbar thickness is 13px via GUI.skin in this editor skin.

Purpose: the preview quad must center in what the user actually sees and never paint outside the scroll view.
Output: edited NamerEditorWindow.cs (fields + 2 OnGUI captures + replaced Repaint block + new pure helper), edited NamerChannelView.shader (3 small edits), edited NamerEditorWindowSmokeTests.cs (4 new tests).

USER-APPROVED DESIGN — implement exactly as specified below. Do not redesign, do not simplify, do not add speculative guards.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@./CLAUDE.md
@Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
@Packages/com.graffitientertainment.namer/Shaders/NamerChannelView.shader
@Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs
@Packages/com.graffitientertainment.namer/Editor/AssemblyInfo.cs
</context>

<critical_constraints>
1. **NEVER run `git commit`, `git add`, `git add -A`, or any state-changing git command.** The working tree is mixed with unrelated dirty files (.gitignore, .planning/REQUIREMENTS.md, deleted .planning/todos/plan-sobel-led-roughness-redesign.md, Assets/NAMER/NamerSmoke.unity, Assets/NAMERGenerated/Neo-T-Pose/*, untracked .claude/, .planning/INBOX-TRIAGE.md, Assets/Stylized Assets/, etc.) that must be preserved untouched and unstaged. The ORCHESTRATOR owns all commits.
2. **The executor may NOT run Unity tests, trigger an asset refresh, focus Unity, or invoke unity-mcp.** The interactive editor holds the project lock; the ORCHESTRATOR runs the EditMode tests live after implementation (04.2-P04 / 260926-lqr precedent). Executor verification is read-only: grep gates plus re-reading the edited regions. Do NOT attempt CLI/batchmode Unity runs.
3. **Edit ONLY the three files in `files_modified`.** No .meta files (no new assets are created — all edits are to existing files). NO changes to DrawChannelPanes, ShowChannelPopup, EnsureChannelViewMaterial, the stylize pipeline, or the volume writer.
4. **Minimal-change discipline** (project rule): the row reservation (line ~2231, GUILayoutUtility.GetRect(0f, kAlbedoPreviewHeight, ...)) stays as-is — it only reserves vertical space; its width no longer matters. The neutral GUI.Box else-branch (~2278) stays as-is. The caption row (~2285-2302) and the empty-reason block stay as-is. The four property sets at ~2252-2255 (SurfaceMap/BaseResidualMap/OcclusionStrength/Channel) stay as-is. No renames, no refactors, no new classes beyond the one specified static helper.
5. **C# house style** (Unity 6 / C# 9): Allman braces, braces on single statements, every variable initialized, Doxygen header on the new helper, kCamelCase constants. Scrollbar thicknesses come from GUI.skin (GUI.skin.verticalScrollbar.fixedWidth / horizontalScrollbar.fixedHeight), never literals; the 0.5f hbar-threshold epsilon is a trivial numeric allowed inline with a comment. This is Unity C# — the C++ rules in the global profile do not apply; the minimal-change rule does.
6. Line anchors below are from the current working tree; locate edits by the quoted content anchors primarily, line numbers secondarily.
</critical_constraints>

<interfaces>
<!-- Current code the executor edits (read from the working tree). Executor should use these directly — no codebase exploration needed. -->

NamerEditorWindow.cs — shader property IDs (lines ~127-128):
```csharp
private static readonly int ChannelId = Shader.PropertyToID("_Channel");
private static readonly int WireColorId = Shader.PropertyToID("_WireColor");
```

NamerEditorWindow.cs — instance fields (line ~233):
```csharp
private Vector2 _scrollPosition;
```
(_channelViewMaterial is at line ~187.)

NamerEditorWindow.cs — OnGUI (lines ~1272-1291):
```csharp
_scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
DrawStepSwitches();
DrawSourceSection();
DrawPreviewSection(inspection);
DrawRoughnessExtractionSection(inspection);
DrawAoSection(inspection);
DrawStylizationSection(inspection);
DrawDecompositionSection(inspection);
DrawOutputSection();
EditorGUILayout.EndScrollView();

DrawActionSection(inspection);
```

NamerEditorWindow.cs — DrawAlbedoPreview Repaint block being REPLACED (lines ~2257-2271):
```csharp
// Aspect-fit sub-rect inside the reserved row: uniform scale from the
// stylize base keeps its proportions (no stretch) and centers it in the
// dialog; vertical centering is a no-op when height is the binding side.
float fitScale = Mathf.Min(rect.width / stylizeBase.width, rect.height / stylizeBase.height);
float drawWidth = stylizeBase.width * fitScale;
float drawHeight = stylizeBase.height * fitScale;
Rect drawRect = new Rect(
    rect.x + (rect.width - drawWidth) * 0.5f,
    rect.y + (rect.height - drawHeight) * 0.5f,
    drawWidth,
    drawHeight);
if (Event.current.type == EventType.Repaint)
{
    Graphics.DrawTexture(drawRect, Texture2D.whiteTexture, _channelViewMaterial);
}
```
(the property sets EnsureChannelViewMaterial/SetTexture x2/SetFloat x2 at ~2251-2255 immediately above stay unchanged; DrawAlbedoPreview ends at ~2312, EnsureProfileEditor's doc header starts ~2314 — place the new helper between them)

NamerChannelView.shader — Properties tail (lines ~12-13):
```shaderlab
_Channel("Channel", Float) = 0
_OcclusionStrength("Occlusion Strength", Range(0.0, 1.0)) = 1.0
```

NamerChannelView.shader — HLSL decl + vertex (lines ~54, ~68-76):
```shaderlab
float _Channel;
...
// Standard Graphics.DrawTexture quad vertex — raw UVs, no TRANSFORM_TEX (the panes
// show the raw texture).
Varyings ChannelVert(Attributes input)
{
    Varyings output;
    output.positionCS = TransformObjectToHclip(input.positionOS.xyz);
    output.uv = input.uv;
    return output;
}
```
(_Channel lives OUTSIDE the UnityPerMaterial CBUFFER — an immediate-mode per-draw property; _UvRect mirrors that placement exactly.)

NamerEditorWindowSmokeTests.cs — namespace GraffitiEntertainment.Namer.Tests, class NamerEditorWindowSmokeTests, usings already include GraffitiEntertainment.Namer.Editor and UnityEngine; 8 existing [Test]s; StylizePreviewBase_PrefersSourceOverProjected ends ~247, "// -- EditorPrefs isolation" banner at ~249. InternalsVisibleTo("GraffitiEntertainment.Namer.Tests.Editor") confirmed at Editor/AssemblyInfo.cs line 5 — the internal helper is called DIRECTLY (no reflection seam needed for it).
</interfaces>

<tasks>

<task type="auto" tdd="true">
  <name>Task 1: View-band capture + banded draw + _UvRect window (NamerEditorWindow.cs, NamerChannelView.shader)</name>
  <files>Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs, Packages/com.graffitientertainment.namer/Shaders/NamerChannelView.shader</files>
  <behavior>
    - The preview quad's horizontal center is scroll.x + viewWidth/2 (visible band), never the reserved row's center
    - drawRect is the band-clipped quad; the clipped-off region is removed from the SAMPLING via uvRect, so the visible portion shows at correct aspect (no stretch)
    - Degenerate band (drawRect width or height <= 0, including inverted rects when the row is fully scrolled out) draws nothing; first frame (captured scroll rect height 0) draws nothing
    - _channelViewMaterial always carries identity _UvRect (0,0,1,1) at the START of every frame: set immediately before the preview draw, reset immediately after (panes draw earlier next frame and share the material)
    - Helper is pure static geometry: no GUI.*, no instance state, no allocation beyond Rect/float locals
  </behavior>
  <action>
Seven targeted edits across two files.

EDIT 1 — NamerEditorWindow.cs property IDs (after ChannelId, line ~127): add `private static readonly int UvRectId = Shader.PropertyToID("_UvRect");`

EDIT 2 — NamerEditorWindow.cs instance fields (next to `private Vector2 _scrollPosition;`, line ~233): add ONE initialized field — `private Rect _scrollViewRect = new Rect(0f, 0f, 0f, 0f);` (default height 0 makes the first-frame guard skip the draw) with a one-line comment stating what it captures (scroll-view widget rect, for the visible-band math). NOTE: the orchestrator dropped the approved set's `_scrollContentHeight` capture — the final design derives vbar presence unconditionally (content is always taller than the view in this window) and hbar presence from the row-width law, so the field would be dead code (CS0414). Do NOT add _scrollContentHeight anywhere.

EDIT 3 — NamerEditorWindow.cs OnGUI (lines ~1287-1288): immediately AFTER EditorGUILayout.EndScrollView() insert `_scrollViewRect = GUILayoutUtility.GetLastRect();` (probe-verified: called immediately after EndScrollView it returns the scroll-view widget rect, whose bottom edge ends where DrawActionSection begins). One comment line: the widget rect feeds the visible-band math in DrawAlbedoPreview; rects INSIDE the scroll view are content-space, so the band is expressed in the same coords as the row rect.

EDIT 4 — NamerEditorWindow.cs DrawAlbedoPreview: REPLACE the aspect-fit/center/draw block (lines ~2257-2271, quoted in interfaces) with the view-band block, keeping the four property sets above it untouched. New block, in order:
(a) First-frame guard `if (_scrollViewRect.height > 0f)` wrapping everything below (the default Rect height 0 skips the draw until OnGUI has captured the widget rect).
(b) `float vbarW = GUI.skin.verticalScrollbar.fixedWidth;` and `float hbarH = GUI.skin.horizontalScrollbar.fixedHeight;`
(c) `float viewWidth = _scrollViewRect.width - vbarW;` with a comment: the vertical scrollbar is always shown in this window (the foldout content is always far taller than the view), so the subtract is unconditional by design.
(d) `bool hbarShown = rect.width > viewWidth + 0.5f;` with a comment: exact overflow detector — an expand row pins at exactly viewWidth when the content fits and stretches to contentWidth past it (probe-verified); 0.5f is a float-noise epsilon.
(e) `float viewHeight = _scrollViewRect.height - (hbarShown ? hbarH : 0f);`
(f) Call `ComputeAlbedoPreviewGeometry(rect, stylizeBase.width, stylizeBase.height, viewWidth, viewHeight, _scrollPosition, out Rect drawRect, out Rect uvRect);`
(g) Repaint draw guarded by `drawRect.width > 0f && drawRect.height > 0f && Event.current.type == EventType.Repaint`: set `_channelViewMaterial.SetVector(UvRectId, new Vector4(uvRect.x, uvRect.y, uvRect.width, uvRect.height));`, then `Graphics.DrawTexture(drawRect, Texture2D.whiteTexture, _channelViewMaterial);`, then IMMEDIATELY reset `_channelViewMaterial.SetVector(UvRectId, new Vector4(0f, 0f, 1f, 1f));` with a comment: the material is shared with DrawChannelPanes (panes draw earlier in the frame than the preview); a stale _UvRect would corrupt pane draws next frame.
Replace the old block's comment with one stating: GUILayout rects inside the scroll view are content-space and the expand row stretches to the CONTENT width; center against the visible band and clip explicitly because Graphics.DrawTexture does not clip to the scroll view (it overdraws the horizontal scrollbar and the action header).

EDIT 5 — NamerEditorWindow.cs new helper, placed immediately after DrawAlbedoPreview's closing brace (~2312), before the EnsureProfileEditor doc header. Doxygen header describing: pure view-band geometry for the albedo preview — aspect-fits the texture into the visible scroll band, centers on the VISIBLE width (the reserved row stretches to the scroll content width, not the panel width), clips the quad to the band, and reports the visible fraction as a UV sub-rect for the _UvRect shader window; headless-testable, no GUI state. Signature exactly:
`internal static void ComputeAlbedoPreviewGeometry(Rect rowRect, float textureWidth, float textureHeight, float viewWidth, float viewHeight, Vector2 scroll, out Rect drawRect, out Rect uvRect)`
Body semantics (exact):
- fitScale = Mathf.Min(viewWidth / Mathf.Max(1f, textureWidth), rowRect.height / Mathf.Max(1f, textureHeight))
- drawWidth = textureWidth * fitScale; drawHeight = textureHeight * fitScale
- Unclipped fit rect: x = scroll.x + (viewWidth - drawWidth) * 0.5f (NOT rowRect.x + (rowRect.width - drawWidth) * 0.5f — that is the bug being fixed); y = rowRect.y + (rowRect.height - drawHeight) * 0.5f (vertical centering within the row, unchanged)
- band = new Rect(scroll.x, scroll.y, viewWidth, viewHeight)
- clipped = Rect.MinMaxRect(Mathf.Max(fit.xMin, band.xMin), Mathf.Max(fit.yMin, band.yMin), Mathf.Min(fit.xMax, band.xMax), Mathf.Min(fit.yMax, band.yMax))
- uvRect from the fit rect with guarded denominators (float fitW = Mathf.Max(1f, fit.width); float fitH = Mathf.Max(1f, fit.height);): uvRect.x = (clipped.xMin - fit.xMin) / fitW; uvRect.y = (clipped.yMin - fit.yMin) / fitH; uvRect.width = clipped.width / fitW; uvRect.height = clipped.height / fitH
- drawRect = clipped (out). A fully-out-of-band row yields an inverted/empty rect (width or height <= 0) — the caller's degenerate check skips the draw.

EDIT 6 — NamerChannelView.shader Properties: after the _OcclusionStrength line (~13) add, with a one-line comment (immediate-mode per-draw window set by the albedo preview clip; identity default keeps every other consumer byte-identical): `[HideInInspector] _UvRect("UV Rect", Vector) = (0,0,1,1)`

EDIT 7 — NamerChannelView.shader HLSL/vertex: add `float4 _UvRect;` directly after `float _Channel;` (~line 54, same immediate-mode placement outside the UnityPerMaterial CBUFFER); replace `output.uv = input.uv;` (~line 74) with `output.uv = input.uv * _UvRect.zw + _UvRect.xy;` and update the two-line comment above the function to mention the identity-default sub-rect window used by the albedo preview's band clip (raw UVs when _UvRect == (0,0,1,1)).
  </action>
  <verify>
    <automated>cd "/Users/Shared/SSDevelopment/Development/Namer-Unity" && grep -n "UvRectId\|_scrollViewRect\|ComputeAlbedoPreviewGeometry\|verticalScrollbar.fixedWidth\|horizontalScrollbar.fixedHeight\|hbarShown" Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs && grep -n "_UvRect" Packages/com.graffitientertainment.namer/Shaders/NamerChannelView.shader && grep -c "rect.x + (rect.width - drawWidth)" Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs; grep -c "output.uv = input.uv;" Packages/com.graffitientertainment.namer/Shaders/NamerChannelView.shader</automated>
  </verify>
  <done>
Both greps list the new sites: ComputeAlbedoPreviewGeometry appears exactly twice in NamerEditorWindow.cs (definition + one call site); UvRectId at 3 sites (decl, set, reset); _scrollViewRect at 4+ sites (decl, OnGUI capture, height guard, viewWidth, viewHeight); NO _scrollContentHeight anywhere (dropped — would be dead code); the shader shows _UvRect at 3 sites (Properties, HLSL decl, ChannelVert). Final count gates: "rect.x + (rect.width - drawWidth)" == 0 in NamerEditorWindow.cs (old content-width centering gone) and "output.uv = input.uv;" == 0 in the shader. The row reservation, GUI.Box else-branch, caption row, property sets ~2251-2255, DrawChannelPanes, and ShowChannelPopup are untouched; no git state-changing commands were run.
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 2: 4 geometry tests against ComputeAlbedoPreviewGeometry (NamerEditorWindowSmokeTests.cs)</name>
  <files>Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs</files>
  <behavior>
    - CentersInViewWidth_NotRowWidth: horizontal center of the returned drawRect is scroll.x + viewWidth/2 (= 244 for viewWidth 488), NOT the 900px row center (450)
    - NeverWiderThanView_WideTexture: a 4096x512 texture in a 488px band fits at drawWidth == 488 exactly; drawRect.width <= viewWidth and uvRect.width == 1
    - BandClip_CutsQuadAndReportsUvSubRect: a quad extending above the band top is cut at the band edge (clipped.yMin == 2200) and uvRect reports the matching visible fraction (uvRect.y == 188/488, uvRect.height == 300/488 < 1, bottom uncut so uvRect.yMax == 1)
    - FullyVisible_PassesIdentityUv: a fully visible row returns the uncut fit rect and uvRect == (0, 0, 1, 1) exactly
  </behavior>
  <action>
Add 4 NUnit [Test] methods to NamerEditorWindowSmokeTests.cs, placed after StylizePreviewBase_PrefersSourceOverProjected (~line 247) and before the "-- EditorPrefs isolation" banner (~249). Each calls `NamerEditorWindow.ComputeAlbedoPreviewGeometry(...)` DIRECTLY (internal access via InternalsVisibleTo — no reflection, matching how internal constants are consumed elsewhere) and gets a Doxygen doc header in the file's established style: hand-derived expectation formulas in comments beside the assertions. Exact fixtures (deterministic — every value below is an exact binary float; tolerance 0.0001f except where noted):

TEST 1 — CentersInViewWidth_NotRowWidth: rowRect = new Rect(0f, 1000f, 900f, 512f), texture 1024x1024, viewWidth 488f, viewHeight 400f, scroll (0f, 0f). Derived: fitScale = min(488/1024, 512/1024) = 61/128, drawWidth == drawHeight == 488, x = 0 + (488-488)/2 = 0. Assert AreEqual(viewWidth * 0.5f, drawRect.center.x, 0.0001f) with a message that the 900px row center (450) was the pre-fix behavior (the row is intentionally below the vertical band here — vertical clip inverts and is irrelevant to the horizontal claim).

TEST 2 — NeverWiderThanView_WideTexture: rowRect = new Rect(0f, 0f, 900f, 512f), texture 4096x512, viewWidth 488f, viewHeight 400f, scroll (0f, 0f). Derived: fitScale = 488/4096 = 61/512, drawWidth == 488 exact, drawHeight == 61. Assert AreEqual(488f, drawRect.width, 0.0001f) plus AreEqual(1f, uvRect.width, 0.0001f) (x-extent unclipped: unclipped x == band x == [0, 488]).

TEST 3 — BandClip_CutsQuadAndReportsUvSubRect: rowRect = new Rect(0f, 2000f, 900f, 512f), texture 1024x1024, viewWidth 488f, viewHeight 300f, scroll (0f, 2200f). Derived: fit 488x488 at y = 2000 + (512-488)/2 = 2012, band y [2200, 2500] — top cut, bottom edge lands exactly on 2500. Assert AreEqual(2200f, drawRect.yMin, 0.0001f); AreEqual((2200f - 2012f) / 488f, uvRect.y, 0.0001f); AreEqual(300f / 488f, uvRect.height, 0.0001f); AreEqual(1f, uvRect.yMax, 0.0001f) with a comment that the quad bottom aligns exactly with the band bottom so only the top is cut. DEVIATION NOTE (flagged to orchestrator): the approved prose said "uvRect.yMax < 1", but with the approved inputs the quad bottom (2012 + 488 = 2500) coincides with the band bottom (2200 + 300 = 2500), so the true invariant is uvRect.yMax == 1 with uvRect.height < 1 — the assertions above pin the mathematically correct values.

TEST 4 — FullyVisible_PassesIdentityUv: rowRect = new Rect(0f, 1000f, 900f, 512f), texture 1024x1024, viewWidth 488f, viewHeight 600f, scroll (0f, 1000f) (scroll.y == rowRect.y — row fully inside the band). Derived: fit 488x488 at y 1012..1500 inside band [1000, 1600], x [0, 488] inside band [0, 488] — uncut. Assert uvRect.x == 0f, uvRect.y == 0f, uvRect.width == 1f, uvRect.height == 1f with NO tolerance (0/488 and 488/488 are exact floats) plus AreEqual(1012f, drawRect.yMin, 0.0001f) and AreEqual(488f, drawRect.height, 0.0001f).

No existing test is modified. No new usings are needed (GraffitiEntertainment.Namer.Editor, NUnit.Framework, UnityEngine already imported).
  </action>
  <verify>
    <automated>cd "/Users/Shared/SSDevelopment/Development/Namer-Unity" && grep -n "CentersInViewWidth_NotRowWidth\|NeverWiderThanView_WideTexture\|BandClip_CutsQuadAndReportsUvSubRect\|FullyVisible_PassesIdentityUv" Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs && grep -c "ComputeAlbedoPreviewGeometry" Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs && grep -c "\[Test\]" Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs</automated>
  </verify>
  <done>
All 4 new test names listed; ComputeAlbedoPreviewGeometry call count in the test file is exactly 4 (one per test); total [Test] count is 12 (8 existing + 4 new); the fixtures carry the exact derived values above; no existing test body touched; no git state-changing commands were run.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| scroll-view layout state -> draw geometry | widget rect / content rect captured from GUILayout; wrong-space math drew over sibling UI (the defect being fixed) |
| shared editor material (_channelViewMaterial) | mutated per-draw by two consumers (panes earlier, preview later in the frame) |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-q260926-mtv-01 | Tampering / correctness | stale _UvRect on the shared channel-view material | mitigate | set immediately before the preview draw, reset to identity (0,0,1,1) immediately after; identity default in the shader keeps panes/popup consumers byte-identical |
| T-q260926-mtv-02 | Denial of Service | degenerate geometry inputs (zero-size texture, inverted band rects) | mitigate | guarded denominators Mathf.Max(1f, ...) in the helper; caller skips the draw when drawRect width/height <= 0 |
| T-q260926-mtv-03 | Repudiation | unmeasured fix rationale | accept | probe facts measured live 2026-09-26 and recorded in the objective; orchestrator re-verifies visually + via tests |
| T-q260926-mtv-SC | Tampering | package installs | accept | no package installs in this task |
</threat_model>

<verification>
Executor (read-only, no editor access):
1. Run both grep blocks in Task 1/Task 2 verify elements. Expected: every listed site present; the two final count gates return 0 (old centering pattern, old raw-UV vert line); test file shows 4 helper calls and 12 [Test] attributes.
2. Re-read the three edited regions (NamerEditorWindow.cs OnGUI + DrawAlbedoPreview + helper; shader Properties/HLSL/vert; the 4 new tests) and walk this checklist: _scrollViewRect captured AFTER EndScrollView (no _scrollContentHeight anywhere); first-frame guard on _scrollViewRect.height > 0f; UvRectId set AND reset inside the Repaint guard; helper centers x on scroll.x + (viewWidth - drawWidth) * 0.5f (not rowRect-based x); uvRect computed from the UNCLIPPED fit rect with Max(1f, ...) denominators; shader _UvRect at 3 sites with identity default.
3. Confirm with read-only `git status --porcelain` that the only NEW modifications attributable to this session are the three files_modified — all other dirty entries pre-existed and were not touched or staged.

Orchestrator (after implementation, live editor):
- Compile check via the live editor, then run the EditMode class NamerEditorWindowSmokeTests as a targeted groupNames run (live-editor RunCommand pattern; no Refresh; file markers not console logs). Expect 12/12 green (8 existing + 4 new).
- Visual probe (dynamic throwaway window or the real window): wide window — quad centered in the visible panel, not offset right; narrow window with horizontal overflow — quad clipped at the scroll-view edge with correct aspect (no stretch), horizontal scrollbar and action header no longer overdrawn; scroll the preview row fully out of view — nothing draws.
- Orchestrator owns the git commit(s) for the mixed working tree.
</verification>

<success_criteria>
- Preview quad centers on the visible band: drawRect center x == scroll.x + viewWidth/2, never the content-width row center (pinned by CentersInViewWidth_NotRowWidth)
- Quad width never exceeds the visible band width; clipped regions are cut from sampling via uvRect, not stretched (pinned by NeverWiderThanView_WideTexture and BandClip_CutsQuadAndReportsUvSubRect)
- No overdraw of the horizontal scrollbar or action header; fully-out-of-band rows draw nothing
- Channel panes and channel popup render identically to before (identity _UvRect, reset after preview draw)
- Fully-visible rows sample the full texture (uvRect exactly (0,0,1,1))
- NamerEditorWindowSmokeTests 12/12 green in the orchestrator's live-editor run
- No commits, no Unity interaction, and no file changes by the executor beyond the three targets
</success_criteria>

<output>
Create `.planning/quick/260926-mtv-albedo-preview-view-band-centering/260926-mtv-SUMMARY.md` when done
</output>
