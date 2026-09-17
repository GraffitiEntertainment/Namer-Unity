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
    /// pair, never camera distance or FOV. The initial size frames EACH pane's own object
    /// with a <see cref="MarginPx"/> screen-space margin under the rotation-invariant
    /// bounding-sphere bound (holds at any orbit), and the pane centers track the
    /// orthographic size so each object stays framed in its pane while zooming (GAP-2).
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

        // The rotation-invariant bounding-sphere radius (extents.magnitude) of the framed
        // mesh. The degenerate default (1f) only matters before the first Frame; Render
        // always re-fits when the mesh changes.
        private float _framedRadius = 1f;

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
            // bounds center keeps each pane's object centered on the ±pane-center offset
            // while it spins (camera fixed — Unity Inspector-preview semantics). The pane
            // centers are derived from the orthographic size, so each object stays framed
            // in its own pane while zooming.
            Quaternion meshRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 rotatedBoundsCenter = meshRotation * beforeMesh.bounds.center;
            float paneCenterWorld = orthoSize * aspect / 2f;
            Vector3 beforePosition = new Vector3(-paneCenterWorld, 0f, 0f) - rotatedBoundsCenter;
            Vector3 afterPosition = new Vector3(paneCenterWorld, 0f, 0f) - rotatedBoundsCenter;

            _preview.DrawMesh(beforeMesh, beforePosition, meshRotation, before, 0);
            _preview.DrawMesh(afterMesh, afterPosition, meshRotation, after, 0);

            // allowScriptableRenderPipeline = true is REQUIRED for URP materials —
            // the default false path renders them magenta/fallback-error.
            _preview.Render(true);
            return _preview.EndPreview();
        }

        /// <summary>
        /// Re-fits the framing to <paramref name="mesh"/>'s rotation-invariant
        /// bounding-sphere radius so each pane's own object stays in view with a
        /// <see cref="MarginPx"/> screen-space margin at ANY yaw+pitch (D-09: framing
        /// re-fits on selection change). Resets zoom, yaw, and pitch.
        /// </summary>
        public void Frame(Mesh mesh)
        {
            if (mesh == null)
            {
                return;
            }

            _framedMesh = mesh;

            float radius = mesh.bounds.extents.magnitude;
            if (radius <= 0f)
            {
                radius = 1f;
            }

            // Rotation-invariant per-pane fit: the bounding-sphere radius bounds the
            // silhouette at ANY yaw+pitch (under pitch the Y extent rotates into the
            // horizontal footprint), so this one fit holds at any orbit — no re-fit on
            // orbit is needed (Frame's existing neutral reset of yaw/pitch is kept).
            _framedRadius = radius;
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
        /// The orthographic half-height that frames the framed object in ONE pane (half the
        /// full rect) with a <see cref="MarginPx"/> screen-space margin on every edge, for
        /// the given <paramref name="aspect"/> (width / height) and
        /// <paramref name="rectHeightPx"/> at the current zoom scale — the same pure
        /// expression <see cref="Render"/> applies to the camera.
        /// </summary>
        public float OrthographicSizeForAspect(float aspect, float rectHeightPx)
        {
            float margin = MarginPx;
            float paneWidthPx = aspect * rectHeightPx / 2f;   // one pane = half the full rect
            float horizFit = _framedRadius * rectHeightPx / Mathf.Max(paneWidthPx - 2f * margin, 1f);
            float vertFit  = _framedRadius * rectHeightPx / Mathf.Max(rectHeightPx - 2f * margin, 1f);
            return Mathf.Max(horizFit, vertFit) * Mathf.Clamp(_zoomScale, MinZoomScale, MaxZoomScale);
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
