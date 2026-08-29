---
status: resolved
trigger: "can't see debug AO texture changing at all, I do see the text say it's processing, but no dialog viewport change at all and I don't see."
created: 2026-08-29T00:00:00Z
updated: 2026-08-29T19:15:00Z
---

## Current Focus

hypothesis: CONFIRMED AND VALIDATED. NamerAOBvh.TraceBvh starts traversal at node 0 (`stack[sp++] = 0`, NamerAOBvh.cs:284) but Build constructs the tree post-order (children appended before parents, NamerAOBvh.cs:194-207) so the root is the LAST node; the root index BuildRecursive returns (NamerAOBvh.cs:89) is discarded. Occluders with > kLeafSize(4) triangles degenerate traversal to the first leaf — rays miss — bake = pure white. One-line clone patch (`stack[sp++] = nodes.Length - 1;`) validated: real-mesh bake went mean=1.000/99.99% white -> mean=0.876/min=0.000/10.78% below 0.5, and every AO sweep now moves surface.b.
test: DONE (diag3, clone patch reverted after run — clone verified byte-identical to repo).
expecting: n/a — investigation complete.
next_action: Deliver ROOT CAUSE FOUND + minimal fix plan. No production code modified (per instructions); fix plan: one-line root-index fix in NamerAOBvh.TraceBvh + two >4-triangle regression tests in NamerAOBakeTests; flagged follow-ups (40 s synchronous bake perf, optional degenerate-bake gate guard, un-multiply tooltip wording).

## Symptoms

expected: Dragging AO controls (Un-multiply Strength / Blur Radius / AO Strength / AO Contrast) in the Processor window while viewing the AO debug channel should visibly change the AO texture in the preview viewport.
actual: No viewport change at all while interacting; status text DOES update ("processing"), so the debounced recompute path runs. Editor console clean (zero errors/exceptions/asserts).
errors: None (console clean, verified via Unity MCP).
reproduction: Open Tools > NAMER > Processor on real Tripo-style source (Neo T-pose under Assets/Models/Neo/, no authored _OcclusionMap), view AO debug channel, drag AO controls.
started: Reported 2026-08-29 during Phase 03.1 UAT (first human test of the new AO extraction controls on real source).
context: Phase 03.1 landed: 72/72 EditMode tests green, 11/11 verification truths pass. Recompute executes without exceptions (triage done). Gap is between "recompute runs" and "viewport visibly changes".

## Eliminated

- hypothesis: H2 — image-space extraction collapses to white on the real bright Tripo texture.
  evidence: Diag on the real base map (first recompute, extraction path): surface.b spans ~0.259..0.999 with ~24% of texels below ~0.49 (min 0.259, mean 0.755) — the extraction has strong, varied structure on this source.
  timestamp: 2026-08-29T01:40Z

- hypothesis: H4 — debug view material samples a stale/previous surface RT after recompute.
  evidence: (a) Window re-binds _SurfaceMap + repaints on every recompute (NamerEditorWindow.cs 286-305); (b) with the white cached bake, surface.b is flat ~white for identity AND every sweep — there is no varying data for a view bug to hide; the user's "no change" is fully explained by the data being constant.
  timestamp: 2026-08-29T01:40Z

- hypothesis: H5 — importer fills _OcclusionMap so the gate takes the authored path.
  evidence: tripo_mat_d83278e6.mat YAML: _OcclusionMap fileID:0; inspection confirmed occlusionMap=NULL.
  timestamp: 2026-08-29T01:15Z

- hypothesis: Broken mesh normals / inverted winding cause the bake's ray misses.
  evidence: Diag2 on the real mesh: zeroNormals=0; windingAgree=13077 vs disagree=4 (degenerate=1). Normals healthy; misses persist with healthy normals — the raycast itself is at fault.
  timestamp: 2026-08-29T02:00Z

## Evidence

- timestamp: 2026-08-29T01:00Z
  checked: NamerEditorWindow.cs RecomputePreview/TriggerAutomaticBake (lines 228-334) + DrawPreviewSection (491-552) + AO sliders (576-660)
  found: Window sets debug material textures fresh on EVERY recompute (line 286-288) and calls Repaint() at the end (line 305). Debug channel AO = toolbar index 2 -> material _DebugChannel 1. After material is `_debugChannel == 0 ? _namerMaterial : _debugMaterial`. Status text "Recomputing preview…" (line 656) shows while `_dirty || _recomputing` — consistent with user seeing "processing". Rebind path is structurally sound.
  implication: H4 (stale binding) has no obvious mechanism at the window level; each recompute re-binds both materials and repaints.

- timestamp: 2026-08-29T01:05Z
  checked: NamerComputePipeline.cs AO gate (117-135) + NamerAOPipeline.cs Extract/BakeAndUpload/RequestBake + bake cache keying
  found: Gate: authored _OcclusionMap > cached bake > extraction. TriggerAutomaticBake runs AFTER every recompute: bakes SYNCHRONOUSLY on first recompute (RequestBake -> BakeAndUpload -> stores cache -> MarkDirty). Bake cache keyed (meshId, occluderId, w, h) — does NOT include tweak settings; cached raw bake is re-tweaked per recompute (ApplyStrengthContrastDirect + ApplyUserBlur on the cached path, lines 212-218). So after the FIRST recompute on a source, the live AO path is ALWAYS the cached bake; extraction runs exactly once per (mesh,occluder,size).
  implication: If the bake ran, user's slider drags reshape the BAKED AO (strength/contrast/blur) and nothing else changes surface.b. If the bake did NOT run (BakeSourceMesh null or bake threw), extraction is the live path. AoUnmultiplyStrength NEVER changes surface.b (only CSNormalize divides base color) — H1 mechanism confirmed in code for that one slider.

- timestamp: 2026-08-29T01:10Z
  checked: NAMERAO.compute CSAoRemap + NAMERAO.hlsl NamerAoRemap + NamerAOBaker.cs
  found: Extracted AO = saturate(lowpass(luma) / globalLumaMean). On a low-variance texture ratio ≈ 1 everywhere -> AO ≈ white. NamerAoRemap(1.0, s, c) = lerp(1,1,s)=1 then (1-0.5)*c+0.5 = 0.5c+0.5 which saturates to white for c>=1 and only grays for c<1 (c=0.5 -> 0.75). Baked AO = unoccluded-fraction rays vs same-mesh BVH, cage 0.01, maxDist 10% of bounds; a mostly-convex T-pose character yields ao≈1 over most of the surface. Debug AO view = surface.b * _OcclusionStrength (NamerSurface.hlsl line 73).
  implication: BOTH synthetic paths (white extraction / near-white bake) make ALL FOUR sliders nearly or fully invisible in the AO debug view — matches "no viewport change at all". Need hard data on which path is live and what its AO distribution is.

- timestamp: 2026-08-29T01:15Z
  checked: Real source assets (Assets/Models/Neo/tripo_mat_d83278e6.mat YAML + PNG/FBX metas)
  found: _OcclusionMap fileID:0 (NOT set), _OcclusionStrength:1, baseMap neo-character_glb_basecolor 2048x2048 sRGB, normal map present, URP/Lit. H5 REFUTED — the authored-map gate branch cannot be taken.
  implication: Live AO path is bake-or-extraction only.

- timestamp: 2026-08-29T01:40Z
  checked: Clone-only diagnostic EditMode test (NamerAoDiagTests) on the REAL Neo source: SourceInspector.Inspect(tripo mat) -> mesh facts -> NamerAOBaker.Bake stats -> exact window sequence via NamerComputePipeline (extract -> RequestBake -> cached bake sweeps of aoStrength/aoContrast/aoBlur/aoUnmultiply) with surface.b + base luma readback stats.
  found: (1) mesh=tripo_node_d83278e6, 16729 verts / 39246 tris, bounds (0.511, 1.000, 0.290). (2) RAW BAKE AT 512: covered 20623/262144 texels = 7.9%; covered-texel AO: min=0.984 (=63/64, exactly ONE occluded ray), max=1.000, mean=1.000, 99.99% >= 0.999 — the bake is PURE WHITE; raycasts essentially NEVER hit the mesh (only 1-2 texels registered a single hit of 64 rays). Bake took 985 ms; RequestBake (JFA at 2048² + readback) 977 ms. (3) EXTRACTION on the real texture (first recompute only): surface.b varies strongly — ~24% of texels below ~0.49 (after correcting the diagnostic's consistent x2 readback scale: min 0.259, max 0.999, mean 0.755) — extraction is GOOD on this source. H2 REFUTED. (4) CACHED-BAKE path (what the user sees after the first recompute): surface.b flat ~0.999 everywhere (frac<250/255 = 0.0000) for identity, strength 0, strength 0.5, contrast 2, blur 16, AND un-multiply 0/1 — base luma IDENTICAL (0.0842) between un-multiply 0 and 1 (dividing by AO=1.0 is a no-op). Only contrast 0.5 produces a change: uniformly scaled to 0.7486 (a flat gray wash).
  implication: H3 CONFIRMED as the dominant mechanism: the geometry bake is all-white for this mesh, the D-07 gate supersedes the good extraction with it after the first recompute, and a white AO makes EVERY AO slider (except contrast<1, which yields a uniform gray) and the un-multiply slider visually no-ops. H1 confirmed as secondary: un-multiply never touches surface.b and with AO=1 the base view cannot change either (base luma equality proven). H4 moot — there is nothing varying in the data to display. NEW OPEN QUESTION: WHY does the bake raycast never hit the real mesh (99.99% zero hits out of 64 rays from covered texels) when the synthetic quad test passes? Candidates: broken mesh normals, BVH/traversal issue at real tri counts, TryReconstruct picking wrong geometry, scale-dependent cage/maxDistance interplay.

- timestamp: 2026-08-29T02:00Z
  checked: Diag2 BVH probe on the real mesh (public NamerAOBvh.Build/RayCast from real triangle centroids, independent brute-force Möller–Trumbore cross-check, normal/winding audit).
  found: mesh 16729 verts / 13082 tris (submesh 0), zeroNormals=0, windingAgree=13077 vs disagree=4. INWARD rays (maxDist 2.0 = twice bounds) hit only 1/2000 centroids — impossible for a closed body. probeD PROOF: for a ray the BVH reports MISS, brute force over all 13k triangles finds 2 hits (minT=0.0100). The BVH traversal misses triangles that exist. Code inspection: TraceBvh hardcodes traversal start node 0 (NamerAOBvh.cs:284) but BuildRecursive appends parents AFTER children (post-order), so the root is nodes.Count-1; the root index returned at NamerAOBvh.cs:89 is discarded. Node 0 = deepest-left leaf (~4 triangles of one corner). Existing bake tests pass only because their occluders have <= 4 triangles (single node, root == 0).
  implication: Root cause of the white bake identified with a direct counterexample (probeD) plus the code mechanism.

- timestamp: 2026-08-29T02:20Z
  checked: Falsifying validation — patched ONLY the clone's NamerAOBvh.cs (`stack[sp++] = nodes.Length - 1;`) and reran the full Diag1 sequence (bake stats + window-sequence sweeps). Clone file then restored to the repo version (verified byte-identical; clone diagnostics deleted).
  found: Real-mesh bake: mean 1.000 -> 0.876, min 0.000, covered-frac <0.5 = 10.78%, <0.75 = 18.10%, <0.9 = 25.94%, <0.98 = 36.85% (was 99.99% >= 0.999). Cached-bake surface.b now varies (identity: min ~0.25 after readback-scale correction, frac<0.98 = 1.18% of the full 2048² texture ~= 8-15% of covered texels) and every sweep moves it: strength 0 -> pure white (by design), strength 0.5 -> min 0.50, contrast 2 -> deeper (min 0.25, baseLuma max 3.67), contrast 0.5 -> mean 0.74, blur 16 -> softened (min 0.36). Un-multiply 0 vs 1: surface.b IDENTICAL (H1 by design) but baseLuma max 0.50 -> 3.22 — the base/shaded views now visibly respond. SIDE FINDING: bake time 985 ms -> 40.5 s with the working BVH (traversal was previously near-free because it only tested ~4 triangles); RequestBake is synchronous on the UI thread, so the fix turns the first-bake hitch (W2) into a ~40 s editor freeze on this mesh class.
  implication: Hypothesis validated end-to-end. Fix restores all UAT Test-1 observability except the AO-channel response to the Un-multiply slider (by design; wording remedy only).

## Resolution

root_cause: NamerAOBvh.TraceBvh begins traversal at node index 0 (NamerAOBvh.cs line 284, `stack[sp++] = 0;`) but NamerAOBvh.Build constructs the hierarchy post-order (children appended before their parent, NamerAOBvh.cs lines 194-207), so the root is the LAST node — the root index returned by BuildRecursive (line 89) is discarded. For any occluder with more than kLeafSize = 4 triangles, traversal therefore only tests the ~4 triangles of the deepest-left leaf, virtually every ray misses, and the geometry bake outputs pure white (real Neo mesh: 99.99% of covered texels AO = 1.0). The Phase 03.1 three-way gate (authored > cached bake > extraction, NamerComputePipeline.cs lines 117-135) then supersedes a good image-space extraction (which the same diagnostics show varies strongly: ~24% of texels below 0.49) with that white bake after the first recompute. With AO pinned at 1.0: the AO debug channel is a flat white image that no AO slider can visibly change (strength/contrast-up/blur all map white to white; only contrast < 1 produces a uniform gray wash), and the AO Un-multiply slider — which by design only divides the base color (CSNormalize, never surface.b) — divides by 1.0, so Base/Shaded views cannot change either (proven: base luma identical at un-multiply 0 vs 1). The existing bake unit tests pass despite the bug because their occluders have <= 4 triangles, where the single node IS the root.
fix: (PLAN — no production code was modified during this session, per instructions)
  1. ROOT-CAUSE FIX (one line): NamerAOBvh.cs, TraceBvh (~line 284): replace `stack[sp++] = 0;` with `stack[sp++] = nodes.Length - 1;` plus a comment stating the invariant (post-order construction => root is the last node; empty-tree case is already guarded by the `nodes.Length == 0` early return above). The static TraceBvh signature cannot take a stored root field because the Burst bake job passes flat arrays only — deriving the root from the array length is the zero-API-change fix and covers both callers (job + RayCast).
  2. REGRESSION TESTS (Packages/.../Tests/Editor/NamerAOBakeTests.cs, plain [Test] — bake is CPU-only, no GPU gate needed, matching sibling tests):
     a. Bvh_TraversalStartsAtRoot_BeyondLeafSizeTriangles — occluder of 6 triangles (floor quad y=0, filler quad y=0.001, roof quad y=0.5) so the largest centroid axis is Y and leaf 0 contains only the 4 lowest-centroid (floor+filler) triangles; assert RayCast from (0.5, 0.01, 0.5) direction (0,1,0) maxDist 1.0 returns true with t ~= 0.49. Fails pre-fix (traversal never reaches the roof leaf), passes post-fix.
     b. Bake_RoofAboveSubdividedFloor_Darkens — same >4-triangle floor+roof through NamerAOBaker.Bake at 64; assert a central covered texel AO < 0.9 (today's equivalent passes only because its occluder has 2 triangles).
  3. FLAGGED FOLLOW-UPS (decisions for the phase owner, NOT part of the minimal fix):
     - PERF/W2: with a working BVH the Neo bake runs ~40 s synchronous on the UI thread (was ~1 s only because the broken traversal did almost no work). Before shipping the fix to users: move the bake off the main thread (schedule the Burst job and complete across editor ticks) or add a progress bar; optionally profile traversal (suspect: unordered child visits and per-access NativeArray safety checks in the editor).
     - HARDENING (optional): degenerate-bake guard — after Bake, if covered texels have (max - min) < epsilon or covered fraction ~= 0, do NOT cache/supersede extraction via the D-07 gate; surface a warning (mirrors the existing occluder-fallback HelpBox pattern in NamerEditorWindow). Would contain any future bake regression.
     - UX/H1 residue: "AO Un-multiply Strength" can never change the AO debug channel (by design it only divides the normalized base). Minimal remedy: tooltip/UAT-script wording ("affects Base Color/Shaded views, not the AO channel"). Post-fix it visibly changes Base/Shaded again because AO < 1.
     - QUALITY WATCH: patched bake shows min AO = 0.000 texels; some may be cage-offset self-hits (near-tangent cosine rays clipping neighboring triangles at ~0.01). If UAT shows speckle, revisit kCageOffset scaling relative to triangle size — separate issue, not part of this fix.
verification: Fix validated by falsification in the clone (only the one line changed): real-source bake distribution became genuine (mean 0.876, 10.78% < 0.5), every AO control moved the packed surface B channel, and un-multiply moved the base channel — exactly the UAT Test-1 expectations. Clone restored to repo-identical state afterwards. For the real fix: run the two new tests red->green plus the full suite via /tmp/namer-gsd/run-tests-in-clone.sh EditMode (expect 72 existing + 2 new green), then re-run UAT Test 1.
files_changed: [none during diagnosis — plan targets Packages/com.graffitientertainment.namer/Editor/Bake/NamerAOBvh.cs (1 line) + Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOBakeTests.cs (2 tests)]

## Fix Applied

status: resolved
applied: 2026-08-29

what_changed:
  1. ROOT-CAUSE FIX — NamerAOBvh.cs TraceBvh now starts traversal at `nodes.Length - 1` (the post-order root) instead of node 0, with a comment stating the invariant. The empty-tree case remains guarded by the earlier `nodes.Length == 0` return.
  2. REGRESSION TESTS — NamerAOBakeTests.cs gained three tests:
     - Bvh_RayCast_HitsTriangleOutsideFirstLeaf — an 8-triangle (4-quad) occluder whose only hittable triangle for the chosen ray sits in the last-built leaf; fails pre-fix, passes post-fix.
     - Bake_RoofAboveSubdividedFloor_Darkens — a 32-triangle subdivided roof occluder above a floor; asserts the central texel AO < 0.9 (pre-fix it was 1.0 exactly).
     - RequestBake_Cancelled_DoesNotCachePartialBake — a headless cancel (injected `shouldCancel`) asserts the bake reports cancellation and does NOT cache a partial result.
  3. CANCELLABLE PROGRESS — NamerAOBaker.Bake now dispatches in row-slabs (deterministic: per-texel work is independent) and polls a `Func<bool> shouldCancel` between slabs; when null it shows `EditorUtility.DisplayCancelableProgressBar` and clears it in finally. Cancellation disposes the partial Ao and returns a `Cancelled` result; NamerAOPipeline.BakeAndUpload/RequestBake thread the flag so a cancelled bake is never cached (the D-07 gate falls back to extraction). NamerEditorWindow surfaces a "bake cancelled" status.

test_results: 75/75 EditMode green (72 existing + 3 new), 0 skipped — /tmp/namer-gsd/run-tests-in-clone.sh EditMode 25.
