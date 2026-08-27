# NAMER Unity Plugin

## Project Summary

Build a Unity-native editor plugin for generating, processing, compressing, previewing, and stylizing NAMER materials directly inside Unity.

The plugin replaces the need to import assets into Blender merely to create NAMER textures or perform texture cleanup.

The existing `GraffitiEntertainment/BlenderNamerPlugin` on the `develop` branch is the reference implementation for the current NAMER encoding and texture-processing concepts. The Unity implementation should preserve format compatibility where useful but should use Unity-native APIs and GPU compute rather than reproduce the Blender implementation.

The primary implementation language is **C#**.

GPU image processing should use **Unity compute shaders/HLSL** where practical.

C++ native plugins are out of scope for the first implementation.

---

# Goals

1. Convert ordinary Unity PBR materials into NAMER materials directly inside Unity.
2. Preserve the current compact NAMER packed texture design.
3. Reduce runtime texture count and memory use.
4. Move low-frequency base color into mesh vertex colors where useful.
5. Preserve residual high-frequency color detail in a smaller texture.
6. Support automatic stylization from a set of visual reference images.
7. Support strong stylized AO and texture smoothing.
8. Provide interactive before/after preview inside the Unity Editor.
9. Never modify imported source assets destructively.
10. Work as a reusable Unity Package Manager package.

---

# Non-Goals for Version 1

Do not require:

* Blender.
* Maya.
* AI object recognition.
* semantic recognition of skin, clothing, trees, etc.
* diffusion models.
* cloud processing.
* C++ native plugins.
* runtime texture conversion.
* destructive editing of imported source files.

AI-assisted processing can be considered later if ordinary image processing proves insufficient.

---

# NAMER Runtime Representation

The current target representation consists primarily of two textures plus optional mesh vertex colors.

## Texture 1 — Base / Color Residual

RGB stores base color or residual color detail.

Initially this may simply contain the cleaned base color.

When vertex-color decomposition is enabled, RGB should store only the color information that cannot be reproduced well through interpolated vertex colors.

Alpha remains available for future use or material-specific data.

## Texture 2 — NAMER Surface Texture

RGBA packing:

* R: octahedrally encoded normal X
* G: octahedrally encoded normal Y
* B: ambient occlusion
* A bit 7: metallic
* A bit 6: emissive
* A bits 0–5: roughness

Roughness therefore has 64 possible values.

Average or primary emissive color may be stored as material metadata rather than as another texture.

The shader reconstructs the tangent-space normal from the octahedral representation.

---

# Source Material Support

Version 1 should support common Unity materials, beginning with:

1. URP Lit
2. Unity Standard where available
3. HDRP Lit if practical without harming the first milestone

The processor should inspect the source material and locate, when available:

* Base Color / Albedo
* Normal
* AO
* Metallic
* Roughness or Smoothness
* Emission
* Alpha / transparency

The processor should also read scalar material properties when separate maps are absent.

Missing maps should use sensible defaults.

Automatic generation of missing maps may be added where reliable.

---

# Generic Material Handling

Avoid a large material taxonomy.

NAMER should mainly preserve material properties already present in the source asset.

Broad material behavior may be inferred from:

* metallic
* roughness / smoothness
* alpha
* transparency
* emission

Optional broad categories may include:

* matte
* glossy
* metallic
* translucent
* emissive

These should normally be inferred rather than manually assigned.

Recognition of semantic classes such as skin, leather, trees, clothing, or stone is not required for version 1.

---

# Processing Pipeline

The editor pipeline should operate approximately as follows:

1. Select model, prefab, material, or folder.
2. Inspect meshes and source materials.
3. Read source PBR textures.
4. Normalize source texture data.
5. Remove unwanted baked lighting from base color where feasible.
6. Generate or normalize AO.
7. Encode normals into octahedral RG representation.
8. Pack AO, metallic, emissive, and roughness into NAMER surface texture.
9. Optionally perform vertex-color decomposition.
10. Optionally perform stylization.
11. Generate derived textures.
12. Generate derived mesh if vertex colors require it.
13. Generate NAMER material.
14. Generate or assign NAMER shader.
15. Preview source and result.
16. Save all generated assets under a dedicated generated-assets directory.

Source assets must never be overwritten.

---

# Vertex Color Decomposition

A major NAMER feature should be reducing the amount of base color that must remain in a texture.

## Principle

Represent low-frequency surface color through vertex colors.

Unity interpolates RGB vertex colors across each triangle.

The remaining color texture stores the residual between:

* the original or stylized base color
* the color reconstructed from interpolated mesh vertex colors

Conceptually:

`BaseColor ≈ VertexColorInterpolation × ResidualColor`

Alternative residual representations may be tested if subtraction or another encoding produces better compression.

## Vertex Color Fitting

Do not simply average each triangle.

For each triangle:

1. Sample the source texture at multiple UV-space points inside the triangle.
2. Compute barycentric coordinates for each sample.
3. Solve for three vertex/corner colors that minimize reconstruction error.
4. Store those fitted colors in the generated mesh.
5. Compute residual texture data from the difference between the fitted interpolation and source texture.

Where shared mesh vertices require different colors because of:

* UV seams
* hard color boundaries
* material boundaries
* discontinuities

the generated mesh may split vertices as needed.

Use `Color32` where precision is sufficient.

## Reconstruction Error

Calculate a reconstruction error for each triangle or region.

Use this metric to determine how much information must remain in the residual texture.

Expose statistics such as:

* vertex-color coverage
* average reconstruction error
* maximum reconstruction error
* estimated residual texture requirement

A debug visualization should optionally display areas with low and high reconstruction error.

---

# Texture Resolution Reduction

Once low-frequency color has moved to vertex colors, determine whether the residual texture can safely use a lower resolution.

Potential source/output examples:

* 4096 → 2048
* 4096 → 1024
* 2048 → 1024

Downsampling should depend on measured reconstruction/detail error rather than use one fixed ratio.

The user should always be able to override the automatic resolution.

---

# Stylization System

Stylization should be an optional stage of NAMER processing.

It should not change the underlying NAMER format.

## Reference Images

Allow the user to assign approximately 3–10 reference images to a style profile.

Reference images define the visual style rather than specific object identities.

A reference set should be analyzed for:

* dominant colors
* hue families
* saturation
* luminance distribution
* shadow colors
* midtone colors
* highlight colors
* warm/cool bias
* contrast
* color count / palette density

Do not simply map every source pixel to the globally nearest dominant color.

Preserve source hue identity.

For example:

* dark brown should map toward a reference-style dark brown
* green should remain within an appropriate green family
* blue should remain within an appropriate blue family

unless the user deliberately chooses a stronger palette replacement mode.

---

# Style Profiles

Create a `NAMERStyleProfile` ScriptableObject.

A profile should contain:

* reference images
* extracted palette data
* style strength
* smoothing strength
* palette compression
* palette mapping strength
* saturation
* contrast
* shadow saturation
* highlight behavior
* AO strength
* painted AO strength
* cavity strength
* normal detail reduction
* roughness simplification
* residual texture settings

Profiles should be reusable across unrelated assets.

Example profiles might include:

* NeoSpace
* SpyWorld
* Stylized Fantasy
* Stylized Sci-Fi

The plugin must not hard-code those specific styles.

---

# Texture Smoothing

Stylization should remove unnecessary high-frequency photographic texture noise while preserving important structures.

Candidate filters include:

* bilateral filtering
* Kuwahara-style filtering
* guided filtering
* custom edge-preserving compute filters

The implementation should favor GPU compute.

The smoothing system should preserve:

* UV island boundaries
* large color transitions
* seams
* graphics
* decals where possible
* meaningful material boundaries

It should reduce:

* photographic grain
* tiny leather variation
* cloth noise
* roughness noise
* unnecessary high-frequency color changes

---

# Ambient Occlusion

Strong AO is an important part of the intended stylized appearance.

NAMER already stores AO in the surface texture.

The stylizer should support two distinct AO concepts.

## Runtime AO

AO stored in the NAMER texture and applied in the shader.

## Painted AO

An optional amount of softened or exaggerated AO blended into the generated base color or vertex colors.

This can give the model a painted/stylized appearance while still retaining runtime AO.

Expose controls for both separately.

Avoid crushing cavities to black unless explicitly requested.

---

# Normal Processing

The source normal map should be converted into the NAMER octahedral representation.

Stylization may optionally reduce small normal detail while preserving large surface structure.

Expose a `Normal Detail` or similar control.

At maximum preservation, retain the original normal detail.

At stronger stylization levels:

* suppress small bumps
* retain folds
* retain panel forms
* retain major surface shape

---

# Roughness Processing

Roughness is stored using six bits in the NAMER packed alpha channel.

The stylizer may simplify roughness variation before quantization.

This is intended to reduce photographic PBR noise and produce cleaner stylized materials.

The source material remains responsible for indicating whether a surface is broadly matte or glossy.

No semantic material recognition should be necessary.

---

# Emissive Processing

NAMER stores an emissive flag in the packed texture.

Emissive color should normally be stored in the generated material or associated metadata.

The processor should preserve existing emission when supplied by the source material.

Automatic emissive detection may be supported as an optional utility.

---

# Unity Editor Interface

Create a NAMER editor window.

Possible menu location:

`Tools > NAMER > Processor`

Main workflow:

## Source

* selected GameObject
* selected prefab
* selected FBX/model
* selected materials
* selected asset folder

## Processing

Controls should initially include:

* Style Strength
* Texture Smoothing
* Palette Size
* Palette Mapping
* AO Strength
* Painted AO
* Cavity Strength
* Normal Detail
* Roughness Simplification
* Vertex Color Decomposition
* Residual Texture Resolution

## Style

* Style Profile
* 3–10 reference images
* Reanalyze Style button

## Output

* destination folder
* generated asset prefix/suffix
* overwrite-generated-assets option

Never overwrite original imported source files.

---

# Preview

The editor should provide immediate visual feedback.

At minimum support:

* original material
* NAMER material
* stylized NAMER material

Prefer previewing the actual selected mesh rather than only a sphere.

Useful views:

* final
* vertex color only
* residual color only
* reconstructed base color
* AO
* normal
* roughness
* metallic
* emissive
* reconstruction error

Where practical, processing controls should update the preview interactively.

---

# GPU Processing

High-resolution texture processing should run primarily through compute shaders.

Likely compute operations include:

* edge-preserving smoothing
* palette remapping
* color quantization
* AO manipulation
* normal filtering
* residual texture calculation
* texture packing
* downsampling
* error measurement

CPU-side C# should handle:

* Unity asset inspection
* editor UI
* mesh processing
* palette/style analysis where appropriate
* command dispatch
* asset generation
* metadata
* serialization

Avoid repeated per-pixel C# loops for large textures.

---

# Shader

Create a NAMER runtime shader for URP first.

It should support:

* base/residual texture
* vertex color reconstruction
* octahedral normal decode
* AO
* metallic
* emissive flag
* roughness decode
* emissive color
* transparency where supported

The shader should allow comparison against URP Lit during development.

Stylization should primarily come from processed data, but the shader may expose optional rendering controls such as:

* shadow tint
* rim lighting
* specular simplification
* stylized diffuse response

These should remain optional so NAMER itself does not become tied to one cartoon rendering style.

---

# Generated Asset Layout

Generated data should live separately from source assets.

Example:

```text
Assets/
    Source/
        Alien.fbx

    NAMERGenerated/
        Alien/
            Alien_NAMER.mesh
            Alien_ColorResidual.png
            Alien_NAMER.png
            Alien_NAMER.mat
```

Style profiles may live in:

```text
Assets/NAMER/Styles/
```

---

# Unity Package Layout

Target package:

```text
com.graffitientertainment.namer/
    package.json

    Runtime/
        NAMERMaterialData.cs
        NAMERSettings.cs

    Editor/
        NAMERProcessorWindow.cs
        NAMERProcessor.cs
        NAMERMaterialAnalyzer.cs
        NAMERMeshProcessor.cs
        NAMERVertexColorFitter.cs
        NAMERTextureProcessor.cs
        NAMERStyleAnalyzer.cs
        NAMERAssetGenerator.cs
        NAMERPreview.cs

    Shaders/
        NAMERLit.shader
        NAMERCommon.hlsl

    Compute/
        NAMERSmooth.compute
        NAMERPalette.compute
        NAMERAO.compute
        NAMERNormal.compute
        NAMERResidual.compute
        NAMERPack.compute
        NAMERDownsample.compute

    Tests/
        Editor/
        Runtime/
```

Exact class names may change as the design develops.

---

# Asset Processing Modes

Version 1 should begin with an explicit command:

`Process with NAMER`

Do not automatically process every model on import.

Later, add optional automatic processing through `AssetPostprocessor`.

The user should be able to mark directories or asset labels for automatic NAMER processing.

---

# Compatibility With Blender NAMER

The Unity plugin should use the existing Blender NAMER implementation as a compatibility and algorithm reference.

The goal is not to maintain Blender as a required part of the pipeline.

Unity should become the primary processing environment.

Where both implementations create the same NAMER packed texture, results should decode equivalently.

The Blender implementation can remain useful for Blender-centric workflows.

---

# Validation

Create automated tests for at least:

1. Octahedral normal encoding and decoding.
2. Metallic bit packing.
3. Emissive bit packing.
4. Six-bit roughness packing.
5. AO channel preservation.
6. Vertex color fitting.
7. Residual reconstruction.
8. UV seam behavior.
9. Generated asset paths.
10. Source assets remain unchanged.

Visual regression test assets should also be included.

---

# Performance Targets

Editor processing should remain practical for common 2K and 4K texture sets.

Interactive preview should favor GPU processing.

Runtime shader cost should remain close enough to a standard Unity PBR material that NAMER remains suitable for large scenes.

The runtime format should require no expensive decompression stage outside normal shader operations.

Vertex-color reconstruction should be essentially free relative to ordinary mesh rendering.

---

# Version 1 Success Criteria

Version 1 is complete when a user can:

1. Import a normal textured FBX directly into Unity.
2. Select it.
3. Run `Process with NAMER`.
4. Have Unity identify the PBR source material.
5. Generate the packed NAMER surface texture.
6. Generate the cleaned base texture.
7. Generate a compatible NAMER material.
8. Render it correctly with the NAMER shader.
9. Optionally move broad base color into vertex colors.
10. Generate and use a residual base-color texture.
11. Supply reference images for a style profile.
12. Smooth and palette-map the asset toward the reference style.
13. Increase stylized AO.
14. Preview the result against the original.
15. Save all output without altering source assets.

---

# Suggested Development Phases

## Phase 1 — Unity Package Foundation

* UPM package
* assembly definitions
* Editor window
* source material inspection
* output asset handling
* basic tests

## Phase 2 — NAMER Encoding

* octahedral normal encoding
* AO channel
* metallic/emissive/roughness packing
* NAMER shader decoding
* render comparison against source material

## Phase 3 — Base Texture Normalization

* cleaned base color
* source map normalization
* missing-map defaults
* GPU texture pipeline

## Phase 4 — Vertex Color Decomposition

* triangle sampling
* barycentric least-squares fitting
* vertex splitting where required
* residual generation
* reconstruction error
* adaptive residual resolution

## Phase 5 — Stylization

* reference-image style profile
* palette extraction
* hue-aware mapping
* edge-preserving smoothing
* AO/cavity styling
* roughness and normal simplification

## Phase 6 — Interactive Preview and Workflow

* actual-mesh preview
* debug channels
* live controls
* before/after comparison
* batch processing

## Phase 7 — Production Hardening

* performance work
* large asset testing
* URP compatibility testing
* HDRP evaluation
* import automation
* documentation

---

# Key Design Rules

1. NAMER is the format name.
2. Unity is the primary processing environment.
3. C# and HLSL are the primary implementation languages.
4. Do not require Blender.
5. Do not require AI for version 1.
6. Preserve original source assets.
7. Keep NAMER independent of any single art style.
8. Keep the packed NAMER runtime representation compact.
9. Use vertex colors for low-frequency color where beneficial.
10. Let textures carry detail that geometry colors cannot reproduce.
11. Measure reconstruction quality rather than relying only on fixed settings.
12. Prefer GPU compute for high-resolution image processing.
13. Make stylization optional and profile-driven.
14. Reference images define art direction without requiring semantic object recognition.
