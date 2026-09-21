using System;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// The two per-pane persistent render targets produced by
    /// <see cref="NamerPreviewRenderer.Render(Mesh, Material, Material, Rect)"/>: the before
    /// pane's texture (its own mesh only) and the after pane's texture (its own mesh only).
    /// Each is a persistent <see cref="RenderTexture"/> owned by the renderer — never
    /// <see cref="PreviewRenderUtility"/>'s cached RT — so the window can draw each pane's
    /// own half without cross-pane overlap.
    /// </summary>
    public readonly struct PreviewRenderResult
    {
        public readonly Texture Before;
        public readonly Texture After;

        public PreviewRenderResult(Texture before, Texture after)
        {
            Before = before;
            After = after;
        }

        public bool IsValid => Before != null && After != null;
    }

    /// <summary>
    /// Wraps a single <see cref="PreviewRenderUtility"/> to render the actual selected
    /// mesh twice side-by-side — the source material (before) and the in-memory NAMER or
    /// debug material (after) — through one shared orthographic camera (D-09, 04.2).
    ///
    /// The 04.2 user decision made the preview orthographic: the perspective projection
    /// offset the lighting and silhouette angle between the compared panes and biased the
    /// A/B judgment. Rotation is now one shared two-axis yaw+pitch quaternion applied to
    /// BOTH panes in sync (amended 2026-09-16: pane sync is the invariant, not axis
    /// restriction; pitch is clamped to ±89°). Zoom is orthographic size — a scale of the
    /// pair, never camera distance or FOV. The initial size frames the object's neutral
    /// AABB tightly — <see cref="InnerMarginPx"/> toward the divider, <see cref="MarginPx"/>
    /// on the outer/vertical edges — and each pane's object is divider-anchored (its inner
    /// edge sits exactly <see cref="InnerMarginPx"/> from the divider). Zoom grows each
    /// object outward from the divider — never across it; clipping at the outer/top/bottom
    /// edges is accepted window behavior — and the anchor is fixed at Frame from the neutral
    /// AABB (the first positioning) so orbit spins each model in place about a fixed center;
    /// zoom grows each model outward from the divider; silhouette overflow past the divider at
    /// extreme combined rotations is clipped invisible by the two-cycle per-pane render
    /// (Task 2) (GAP-5).
    /// Orbit rotates each instance IN PLACE about its own bounds center (the standard
    /// Unity object-preview expectation — drag spins the object, camera stays put);
    /// ctrl+drag strafes the shared camera in its view plane (Pan) — the one
    /// camera-transform motion, alongside zoom-as-orthographic-size.
    ///
    /// URP materials render magenta/fallback-error through the built-in preview path, so
    /// every render calls <see cref="PreviewRenderUtility.Render(bool, bool)"/> with
    /// <c>allowScriptableRenderPipeline = true</c> (RESEARCH Pitfall 1). Re-framing
    /// re-fits <see cref="Mesh.bounds"/> only when the framed mesh changes.
    /// </summary>
    public sealed class NamerPreviewRenderer : IDisposable
    {
        public const float MarginPx = 15f;
        public const float InnerMarginPx = 10f;
        public const float ZoomSensitivity = 0.05f;

        private const float MinPitch = -89f;
        private const float MaxPitch = 89f;
        private const float OrthoCameraDistance = 4f;
        private const float DefaultOrthographicSize = 1.5f;
        private const float MinZoomScale = 0.05f;
        private const float MaxZoomScale = 10f;

        private PreviewRenderUtility _preview;
        private Mesh _framedMesh;
        private float _yaw;
        private float _pitch;
        private float _zoomScale = 1f;
        private float _panX;
        private float _panY;

        // The neutral-rotation AABB half-extents of the framed mesh. The degenerate defaults
        // (1f) only matter before the first Frame; Render always re-fits when the mesh changes.
        private float _framedHalfWidth = 1f;
        private float _framedHalfHeight = 1f;

        // Persistent per-pane render targets owned by the renderer (blit targets; reallocc'd
        // on rect-size change, released in Cleanup). Never PreviewRenderUtility's cached RT.
        private RenderTexture _beforePaneTexture;
        private RenderTexture _afterPaneTexture;

        /// <summary>True when the preview camera is orthographic (false before the preview scene exists).</summary>
        public bool IsOrthographic => _preview != null && _preview.camera.orthographic;

        /// <summary>The accumulated yaw of the shared rotation, in degrees.</summary>
        public float YawDegrees => _yaw;

        /// <summary>The accumulated (clamped) pitch of the shared rotation, in degrees.</summary>
        public float PitchDegrees => _pitch;

        /// <summary>The current orthographic-size zoom scale (clamped to [<see cref="MinZoomScale"/>, <see cref="MaxZoomScale"/>]).</summary>
        public float ZoomScale => _zoomScale;

        /// <summary>The orthographic half-height currently applied to the preview camera (0 before the preview scene exists).</summary>
        public float OrthographicSize => _preview != null ? _preview.camera.orthographicSize : 0f;

        /// <summary>
        /// Creates the preview scene with a fixed orthographic camera and neutral ambient
        /// light (RESEARCH code example). No <c>AddManagedGO</c> — that API is not present
        /// in Unity 6.
        /// </summary>
        public NamerPreviewRenderer()
        {
            _preview = new PreviewRenderUtility();
            _preview.camera.orthographic = true;
            _preview.camera.orthographicSize = DefaultOrthographicSize;
            _preview.ambientColor = new Color(0.1f, 0.1f, 0.1f, 1f);
        }

        /// <summary>
        /// Renders <paramref name="mesh"/> twice (before left, after right) into two per-pane
        /// render targets and returns them as a <see cref="PreviewRenderResult"/>.
        /// <see cref="PreviewRenderResult.IsValid"/> is <c>false</c> when the mesh is missing
        /// so the caller can render its empty-state UI instead.
        /// </summary>
        public PreviewRenderResult Render(Mesh mesh, Material before, Material after, Rect rect)
        {
            return Render(mesh, mesh, before, after, rect);
        }

        /// <summary>
        /// Renders two (possibly different) meshes side-by-side — the source mesh (before,
        /// left) and the decomposition split mesh (after, right) — through one shared,
        /// orthographic camera (D-08 after-mesh parity), each into its OWN per-pane
        /// persistent render target via two BeginPreview→DrawMesh→Render(true)→EndPreview
        /// cycles. Framing uses <paramref name="beforeMesh"/> bounds; the split mesh carries
        /// the fitted vertex colors but identical geometry, so both instances stay in view.
        /// Returns a <see cref="PreviewRenderResult"/> whose
        /// <see cref="PreviewRenderResult.Before"/>/<see cref="PreviewRenderResult.After"/>
        /// are the two persistent pane RTs (each cycle's utility RT is blitted into its pane
        /// RT before the next cycle begins); <see cref="PreviewRenderResult.IsValid"/> is
        /// <c>false</c> when either mesh is missing. When <paramref name="wireMesh"/>,
        /// <paramref name="wireMaterial"/>, and a valid <paramref name="wireSubmesh"/> are
        /// given, the after pane also draws the wire as an unlit second pass in the same
        /// cycle — the pane mesh itself (the split mesh's line-topology submesh) OR a
        /// standalone cached wire mesh built from the after mesh (source/generated assets
        /// are never mutated) — so the wireframe is clipped per pixel by the GPU viewport
        /// and follows pan/zoom/orbit for free (the before pane is unaffected).
        /// Framing/rotation/zoom semantics are unchanged.
        /// </summary>
        public PreviewRenderResult Render(Mesh beforeMesh, Mesh afterMesh, Material before, Material after, Rect rect,
                                          Mesh wireMesh = null, Material wireMaterial = null, int wireSubmesh = -1)
        {
            if (beforeMesh == null || afterMesh == null || _preview == null)
            {
                return default;
            }

            EnsureFramed(beforeMesh);
            ApplyCamera();

            // Fit the orthographic size to the rect aspect here (horizontal fit depends on
            // aspect, so the size is applied where the rect is known, not in Frame).
            float aspect = rect.width / Mathf.Max(rect.height, 1f);
            float orthoSize = OrthographicSizeForAspect(aspect, rect.height);

            // Draw each instance rotated IN PLACE about its own bounds center: the draw
            // transform maps v -> rotation * v + position, so countering the rotated
            // bounds center keeps each pane's object anchored on the ±PaneAnchorWorld
            // offset while it spins (camera fixed — Unity Inspector-preview semantics).
            // The anchor derives from the neutral _framedHalfWidth captured at Frame plus the
            // orthographic-size (zoom) term — it does not change under orbit, so the inner
            // edge stays InnerMarginPx from the divider and zoom grows each object outward
            // from the divider (never across it).
            Quaternion meshRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 rotatedBoundsCenter = meshRotation * beforeMesh.bounds.center;
            Vector2 drawOffsets = GetPaneDrawOffsets(aspect, rect.height);

            Texture beforeRT = RenderPane(beforeMesh, before, rect, orthoSize, meshRotation, rotatedBoundsCenter, drawOffsets.x, null, null, -1, ref _beforePaneTexture);
            Texture afterRT = RenderPane(afterMesh, after, rect, orthoSize, meshRotation, rotatedBoundsCenter, drawOffsets.y, wireMesh, wireMaterial, wireSubmesh, ref _afterPaneTexture);
            return new PreviewRenderResult(beforeRT, afterRT);
        }

        /// <summary>
        /// Renders one pane's mesh into its own persistent RT via a single
        /// BeginPreview→DrawMesh→Render(true)→EndPreview cycle. When
        /// <paramref name="wireMesh"/> is given with <paramref name="wireMaterial"/> and a
        /// valid <paramref name="wireSubmesh"/>, the wire mesh is drawn a second time with
        /// that material in the same cycle (wireframe second pass — After pane only; the
        /// wire may be the pane mesh itself via its line-topology submesh, or a standalone
        /// cached wire mesh built from the after mesh — source/generated assets are never
        /// mutated).
        /// </summary>
        private Texture RenderPane(Mesh mesh, Material material, Rect rect, float orthoSize,
                                   Quaternion meshRotation, Vector3 rotatedBoundsCenter, float drawOffsetX,
                                   Mesh wireMesh, Material wireMaterial, int wireSubmesh, ref RenderTexture paneTexture)
        {
            _preview.BeginPreview(rect, GUIStyle.none);
            ApplyCamera();
            _preview.camera.orthographicSize = orthoSize;
            _preview.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            _preview.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
            Vector3 position = new Vector3(drawOffsetX, 0f, 0f) - rotatedBoundsCenter;
            _preview.DrawMesh(mesh, position, meshRotation, material, 0);
            if (wireMesh != null && wireMaterial != null && wireSubmesh >= 0)
            {
                // Wireframe second pass: the split mesh's own line-topology submesh or a
                // standalone wire mesh built from the after mesh — same transform, unlit
                // wire material. GPU viewport clipping cuts edges per pixel at the pane
                // edge, and pan/zoom/orbit follow the camera transform for free.
                _preview.DrawMesh(wireMesh, position, meshRotation, wireMaterial, wireSubmesh);
            }
            _preview.Render(true);   // allowScriptableRenderPipeline=true is REQUIRED for URP materials
            Texture utilityRt = _preview.EndPreview();   // the utility's CACHED RT — reused by the next BeginPreview
            return BlitToPane(ref paneTexture, utilityRt);  // copy NOW, before the next cycle overwrites it
        }

        private RenderTexture BlitToPane(ref RenderTexture pane, Texture source)
        {
            int w = source.width;
            int h = source.height;
            if (pane == null || pane.width != w || pane.height != h)
            {
                if (pane != null)
                {
                    pane.Release();
                }

                pane = new RenderTexture(w, h, 0, source.graphicsFormat)
                {
                    name = "NamerPreviewPane",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            Graphics.Blit(source, pane);
            RenderTexture.active = null;   // restore EndPreview's post-condition — Render runs mid-OnGUI (C-3)
            return pane;
        }

        /// <summary>
        /// Re-fits the framing to <paramref name="mesh"/>'s neutral-rotation AABB
        /// half-extents so each pane's own object hugs the divider with an
        /// <see cref="InnerMarginPx"/> screen-space inner margin (D-09: framing re-fits on
        /// selection change). Resets zoom, yaw, pitch, and pan.
        /// </summary>
        public void Frame(Mesh mesh)
        {
            if (mesh == null)
            {
                return;
            }

            _framedMesh = mesh;

            // Tight neutral-AABB fit: store the axis half-extents at the neutral rotation.
            // The fit is fixed at Frame — anchoring (PaneAnchorWorld), not fitting, absorbs
            // orbit — so no re-fit on orbit is needed (Frame's existing neutral reset of
            // yaw/pitch is kept).
            Vector3 extents = mesh.bounds.extents;
            _framedHalfWidth = extents.x > 0f ? extents.x : 1f;
            _framedHalfHeight = extents.y > 0f ? extents.y : 1f;

            _zoomScale = 1f;
            _yaw = 0f;
            _pitch = 0f;
            _panX = 0f;
            _panY = 0f;

            ApplyCamera();
        }

        /// <summary>
        /// Rotates both preview instances in place by yaw (world up) and pitch (local X)
        /// degrees; pitch is clamped to keep both poles reachable. The camera stays fixed.
        /// </summary>
        public void Orbit(float yawDegrees, float pitchDegrees)
        {
            _yaw += yawDegrees;
            _pitch = Mathf.Clamp(_pitch + pitchDegrees, MinPitch, MaxPitch);
        }

        /// <summary>
        /// Zooms the preview by scaling the orthographic size (positive scroll = zoom in,
        /// i.e. a smaller size), clamped to a sane scale range. Exponential in the delta —
        /// each scroll unit applies a constant ratio, so the step feels uniform across the
        /// zoom range, zoom in/out are exactly reciprocal, and the factor never flips sign
        /// under large trackpad-momentum deltas. The camera transform never moves.
        /// </summary>
        public void Zoom(float scrollDelta)
        {
            _zoomScale = Mathf.Clamp(_zoomScale * Mathf.Pow(1f + ZoomSensitivity, -scrollDelta), MinZoomScale, MaxZoomScale);
        }

        /// <summary>
        /// Strafes the preview view by the given world-space offset (ctrl+drag). Moves the
        /// shared orthographic camera in its view plane; mesh draw positions and the
        /// divider anchor are unchanged.
        /// </summary>
        public void Pan(float worldX, float worldY)
        {
            _panX += worldX;
            _panY += worldY;
        }

        /// <summary>The accumulated world-space pan of the preview camera.</summary>
        public Vector2 PanOffset => new Vector2(_panX, _panY);

        /// <summary>
        /// The orthographic half-height that frames the framed object's neutral AABB in ONE
        /// pane (half the full rect) with <see cref="InnerMarginPx"/> toward the divider and
        /// <see cref="MarginPx"/> on the outer/vertical edges, for the given
        /// <paramref name="aspect"/> (width / height) and <paramref name="rectHeightPx"/> at
        /// the current zoom scale — the same pure expression <see cref="Render"/> applies to
        /// the camera.
        /// </summary>
        public float OrthographicSizeForAspect(float aspect, float rectHeightPx)
        {
            float paneWidthPx = aspect * rectHeightPx / 2f;   // one pane = half the full rect
            float horizFit = _framedHalfWidth * rectHeightPx / Mathf.Max(paneWidthPx - InnerMarginPx - MarginPx, 1f);
            float vertFit  = _framedHalfHeight * rectHeightPx / Mathf.Max(rectHeightPx - 2f * MarginPx, 1f);
            return Mathf.Max(horizFit, vertFit) * Mathf.Clamp(_zoomScale, MinZoomScale, MaxZoomScale);
        }

        /// <summary>
        /// The world-x distance of each pane's object center from the divider (world x = 0):
        /// the neutral X half-extent captured at Frame plus <see cref="InnerMarginPx"/>
        /// expressed in world units at the current orthographic size. Fixed under orbit (the
        /// first positioning) so orbit spins each model in place; still scales with zoom so
        /// zoom grows each object outward from the divider. Overflow past the divider at
        /// extreme combined rotations is clipped invisible by the two-cycle per-pane render
        /// (each pane renders its own mesh into its own RT; the window discards the far half
        /// of each RT).
        /// </summary>
        public float PaneAnchorWorld(float aspect, float rectHeightPx)
        {
            float orthoSize = OrthographicSizeForAspect(aspect, rectHeightPx);
            return _framedHalfWidth + InnerMarginPx * (2f * orthoSize / Mathf.Max(rectHeightPx, 1f));
        }

        /// <summary>
        /// The mirrored world-x draw offsets for the two panes: <c>(−anchor, +anchor)</c>,
        /// where <see cref="PaneAnchorWorld"/> is the divider-anchored distance of each
        /// pane's object center from the divider (world x = 0) at the current zoom
        /// (rotation-stable — fixed at Frame). <see cref="Render"/> draws the before mesh at
        /// <c>.x</c> and the after mesh at <c>.y</c> so each pane's inner edge sits exactly
        /// <see cref="InnerMarginPx"/> from the divider — never centered (GAP-4).
        /// </summary>
        public Vector2 GetPaneDrawOffsets(float aspect, float rectHeightPx)
        {
            float anchor = PaneAnchorWorld(aspect, rectHeightPx);
            return new Vector2(-anchor, anchor);
        }

        /// <summary>Releases the underlying preview scene and camera (idempotent).</summary>
        public void Cleanup()
        {
            if (_preview != null)
            {
                _preview.Cleanup();
                _preview = null;
            }

            if (_beforePaneTexture != null)
            {
                _beforePaneTexture.Release();
                _beforePaneTexture = null;
            }

            if (_afterPaneTexture != null)
            {
                _afterPaneTexture.Release();
                _afterPaneTexture = null;
            }
        }

        /// <summary>Disposes the preview renderer (calls <see cref="Cleanup"/>).</summary>
        public void Dispose()
        {
            Cleanup();
        }

        private void EnsureFramed(Mesh mesh)
        {
            if (!ReferenceEquals(_framedMesh, mesh))
            {
                Frame(mesh);
            }
        }

        private void ApplyCamera()
        {
            if (_preview == null)
            {
                return;
            }

            // Camera stays on its viewing axis (looking at the before/after midpoint,
            // world origin) but strafes in its view plane with Pan; orbit rotation is
            // applied to the mesh instances in Render and zoom scales the orthographic
            // size (the camera transform moves only under Pan).
            _preview.camera.transform.position = new Vector3(_panX, _panY, -OrthoCameraDistance);
            _preview.camera.transform.rotation = Quaternion.identity;
        }
    }
}
