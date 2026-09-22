# NAMER technical preprint

**Title:** NAMER: Compact PBR Materials via Vertex-Color Projection and Detail-to-Roughness Transfer

**Author line:** Kenneth Hurley / Genius Ventures

**Draft date:** September 21, 2026

## Files

- `namer_paper.tex`: self-contained LaTeX manuscript, including two vector diagrams and an embedded bibliography.
- `namer_paper.pdf`: compiled manuscript.
- `references.bib`: optional BibTeX copy of the references for future editing; the current manuscript does not require it to build.
- `AUTHOR_NOTES.md`: implementation findings and decisions to review before circulation or submission.
- `validation/checks.py`: standalone scalar equation checks using the Python standard library.
- `validation/checks_results.json`: recorded output from those checks.

## Compile

Use a normal TeX Live or MiKTeX installation with the packages named in the preamble. No external images, custom fonts, downloaded style files, or shell escape are required.

```sh
latexmk -pdf -interaction=nonstopmode -halt-on-error namer_paper.tex
```

Alternatively:

```sh
pdflatex -interaction=nonstopmode -halt-on-error namer_paper.tex
pdflatex -interaction=nonstopmode -halt-on-error namer_paper.tex
```

For Overleaf, upload the archive or `namer_paper.tex` and select pdfLaTeX. The bibliography is embedded, so a BibTeX run is unnecessary.

The author, affiliation, and date are centralized near the start of the TeX source. PDF metadata has a separate author field in `hypersetup`.

## Reproduce scalar checks

From this directory:

```sh
python3 validation/checks.py --output validation/checks_results.json
```

These are independently transcribed binary64 equation checks. They are **not** runs of the Unity/C# or HLSL implementation, comprehensive simulations of texture-format quantization, rendered-quality evaluations, or performance benchmarks. Recorded numerical results should not be relabeled as GPU test results.

## Implementation snapshot

Repository: `GraffitiEntertainment/Namer-Unity`

Branch inspected: `main`

Pinned commit: `9eb068520b6e5fcf3ff39e13291dedd3f88e0015`

Package version: `0.1.0`

The editable-color-table extension is explicitly described as a planned design. It is not included among the implemented results.

This is a technical preprint draft, not an arXiv submission or endorsement. Nothing has been published or committed to the repository as part of preparing these files.
