# Version 2 changes

- Broadened the title, abstract, introduction, contributions, and conclusion from color compaction to material extraction, separation, representation, and recomposition.
- Added a component-status table distinguishing implemented import/processing from prospective estimators.
- Expanded AO into a full section covering image-space extraction, geometry baking, source precedence, shaping, separation, stage ordering, and recomposition.
- Corrected the AO floor distinction: synthetic 0.1 versus authored/neutral 1e-6.
- Documented authored roughness/smoothness precedence over extraction.
- Added a full planned metallic/emissive extraction section, including masks, shared values, candidate fitting equations, confidence, and cross-channel handling.
- Preserved binary metallic/emissive spatial storage and shared per-material values.
- Updated the workflow diagram and connected color tables to AO, roughness, metallic, and emission controls.
- Extended the evaluation protocol to extraction quality, material masks, and per-material values.
- Added primary references on AO, sampling, intrinsic-image estimation, and material semantics.
- Added separately labeled AO-equation and proposed-value-model checks, with executed JSON output.

- Changed paper affiliation/company name from Graffiti Entertainment to Genius Ventures.

## 2026-09-21 - implementation figures

- Added the Unity `preview-before-after.png` screenshot as the main qualitative before/after figure.
- Added `decomposition-section.png` to document the fit/error/residual diagnostics exposed by the editor.
- Added explicit caption language separating illustrative screenshots from controlled benchmark evidence.
- Bundled the referenced figures with the LaTeX source for a self-contained build.
