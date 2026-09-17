using System;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
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
    /// edges is accepted window behavior — and the anchor recomputes the rotated horizontal
    /// half-extent per Render so orbit never crosses the divider (GAP-4).
    /// Orbit rotates each instance IN PLACE about its own bounds center (the standard
    /// Unity object-preview expectation — drag spins the object, camera stays put).
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
        public const float ZoomSensitivity = 0.15f;

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

        // The neutral-rotation AABB half-extents of the framed mesh. The degenerate defaults
        // (1f) only matter before the first Frame; Render always re-fits when the mesh changes.
        private float _framedHalfWidth = 1f;
        private float _framedHalfHeight = 1f;

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
        /// Renders <paramref name="mesh"/> twice (before left, after right) into an
        /// offscreen texture and returns it. Returns <c>null</c> when the mesh is missing
        /// so the caller can render its empty-state UI instead.
        /// </summary>
        public Texture Render(Mesh mesh, Material before, Material after, Rect rect)
        {
            return Render(mesh, mesh, before, after, rect);
        }

        /// <summary>
        /// Renders two (possibly different) meshes side-by-side — the source mesh (before,
        /// left) and the decomposition split mesh (after, right) — through one shared,
        /// orthographic camera (D-08 after-mesh parity). Framing uses <paramref name="beforeMesh"/>
        /// bounds; the split mesh carries the fitted vertex colors but identical geometry, so
        /// both instances stay in view. Returns <c>null</c> when either mesh is missing.
        /// </summary>
        public Texture Render(Mesh beforeMesh, Mesh afterMesh, Material before, Material after, Rect rect)
        {
            if (beforeMesh == null || afterMesh == null || _preview == null)
            {
                return null;
            }

            _preview.BeginPreview(rect, GUIStyle.none);
            EnsureFramed(beforeMesh);
            ApplyCamera();

            // Fit the orthographic size to the rect aspect here (horizontal fit depends on
            // aspect, so the size is applied where the rect is known, not in Frame).
            float aspect = rect.width / Mathf.Max(rect.height, 1f);
            float orthoSize = OrthographicSizeForAspect(aspect, rect.height);
            _preview.camera.orthographicSize = orthoSize;

            _preview.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            _preview.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);

            // Draw each instance rotated IN PLACE about its own bounds center: the draw
            // transform maps v -> rotation * v + position, so countering the rotated
            // bounds center keeps each pane's object anchored on the ±PaneAnchorWorld
            // offset while it spins (camera fixed — Unity Inspector-preview semantics).
            // The anchor is derived from the orthographic size and the rotated horizontal
            // half-extent, so the inner edge stays InnerMarginPx from the divider and zoom
            // grows each object outward from the divider (never across it).
            Quaternion meshRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 rotatedBoundsCenter = meshRotation * beforeMesh.bounds.center;
            Vector2 drawOffsets = GetPaneDrawOffsets(aspect, rect.height);
            Vector3 beforePosition = new Vector3(drawOffsets.x, 0f, 0f) - rotatedBoundsCenter;
            Vector3 afterPosition = new Vector3(drawOffsets.y, 0f, 0f) - rotatedBoundsCenter;

            _preview.DrawMesh(beforeMesh, beforePosition, meshRotation, before, 0);
            _preview.DrawMesh(afterMesh, afterPosition, meshRotation, after, 0);

            // allowScriptableRenderPipeline = true is REQUIRED for URP materials —
            // the default false path renders them magenta/fallback-error.
            _preview.Render(true);
            return _preview.EndPreview();
        }

        /// <summary>
        /// Re-fits the framing to <paramref name="mesh"/>'s neutral-rotation AABB
        /// half-extents so each pane's own object hugs the divider with an
        /// <see cref="InnerMarginPx"/> screen-space inner margin (D-09: framing re-fits on
        /// selection change). Resets zoom, yaw, and pitch.
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
        /// i.e. a smaller size), clamped to a sane scale range. The camera transform never
        /// moves.
        /// </summary>
        public void Zoom(float scrollDelta)
        {
            _zoomScale = Mathf.Clamp(_zoomScale * (1f - scrollDelta * ZoomSensitivity), MinZoomScale, MaxZoomScale);
        }

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
        /// The rotated-AABB horizontal half-extent (world units) of the framed mesh at the
        /// current yaw/pitch — the absolute first row of the rotation matrix dotted with the
        /// mesh extents. This is the distance from the object's center to its innermost X
        /// silhouette at ANY orbit, so it bounds the inner edge the anchor offsets.
        /// </summary>
        public float HorizontalHalfExtentWorld()
        {
            if (_framedMesh == null)
            {
                return 0f;
            }

            Matrix4x4 m = Matrix4x4.Rotate(Quaternion.Euler(_pitch, _yaw, 0f));
            Vector3 e = _framedMesh.bounds.extents;
            return Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z;
        }

        /// <summary>
        /// The world-x distance of each pane's object center from the divider (world x = 0):
        /// the rotated horizontal half-extent plus <see cref="InnerMarginPx"/> expressed in
        /// world units at the current orthographic size. Recomputes per Render so zoom grows
        /// each object outward from the divider, never across it.
        /// </summary>
        public float PaneAnchorWorld(float aspect, float rectHeightPx)
        {
            float orthoSize = OrthographicSizeForAspect(aspect, rectHeightPx);
            return HorizontalHalfExtentWorld() + InnerMarginPx * (2f * orthoSize / Mathf.Max(rectHeightPx, 1f));
        }

        /// <summary>
        /// The mirrored world-x draw offsets for the two panes: <c>(−anchor, +anchor)</c>,
        /// where <see cref="PaneAnchorWorld"/> is the divider-anchored distance of each
        /// pane's object center from the divider (world x = 0) at the current rotation/zoom
        /// state. <see cref="Render"/> draws the before mesh at <c>.x</c> and the after mesh
        /// at <c>.y</c> so each pane's inner edge sits exactly <see cref="InnerMarginPx"/>
        /// from the divider — never centered (GAP-4).
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

            // Camera fixed on its viewing axis, looking at the before/after midpoint
            // (world origin); orbit rotation is applied to the mesh instances in Render
            // and zoom scales the orthographic size (never the camera transform).
            _preview.camera.transform.position = new Vector3(0f, 0f, -OrthoCameraDistance);
            _preview.camera.transform.rotation = Quaternion.identity;
        }
    }
}
