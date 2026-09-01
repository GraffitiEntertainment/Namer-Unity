using System;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Wraps a single <see cref="PreviewRenderUtility"/> to render the actual selected
    /// mesh twice side-by-side — the source material (before) and the in-memory NAMER or
    /// debug material (after) — through one shared, framed camera (D-09).
    ///
    /// URP materials render magenta/fallback-error through the built-in preview path, so
    /// every render calls <see cref="PreviewRenderUtility.Render(bool, bool)"/> with
    /// <c>allowScriptableRenderPipeline = true</c> (RESEARCH Pitfall 1). The two mesh
    /// instances are drawn symmetric about the world origin, which is the honest
    /// before/after midpoint; framing distance is derived from the mesh bounds plus the
    /// horizontal separation so both instances stay in view. Orbit rotates each instance
    /// IN PLACE about its own bounds center (the standard Unity object-preview
    /// expectation — drag spins the object, camera stays put); zoom moves the fixed
    /// camera along its viewing axis. Re-framing re-fits <see cref="Mesh.bounds"/> only
    /// when the framed mesh changes.
    /// </summary>
    public sealed class NamerPreviewRenderer : IDisposable
    {
        private const float FieldOfView = 30f;
        private const float HalfSeparation = 0.7f;
        private const float MinCameraDistance = 1.5f;
        private const float MaxCameraDistance = 100f;
        private const float ZoomSensitivity = 0.15f;
        private const float MinPitch = -89f;
        private const float MaxPitch = 89f;

        private PreviewRenderUtility _preview;
        private Mesh _framedMesh;
        private float _distance = 4f;
        private float _yaw;
        private float _pitch;

        /// <summary>
        /// Creates the preview scene with a forward preview camera and neutral ambient
        /// light (RESEARCH code example). No <c>AddManagedGO</c> — that API is not present
        /// in Unity 6.
        /// </summary>
        public NamerPreviewRenderer()
        {
            _preview = new PreviewRenderUtility();
            _preview.cameraFieldOfView = FieldOfView;
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
        /// framed camera (D-08 after-mesh parity). Framing uses <paramref name="beforeMesh"/>
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

            _preview.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            _preview.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);

            // Draw each instance rotated IN PLACE about its own bounds center: the draw
            // transform maps v -> rotation * v + position, so countering the rotated
            // bounds center keeps each pane's object centered on the ±separation offset
            // while it spins (camera fixed — Unity Inspector-preview semantics).
            Quaternion meshRotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 rotatedBoundsCenter = meshRotation * beforeMesh.bounds.center;
            Vector3 beforePosition = new Vector3(-HalfSeparation, 0f, 0f) - rotatedBoundsCenter;
            Vector3 afterPosition = new Vector3(HalfSeparation, 0f, 0f) - rotatedBoundsCenter;

            _preview.DrawMesh(beforeMesh, beforePosition, meshRotation, before, 0);
            _preview.DrawMesh(afterMesh, afterPosition, meshRotation, after, 0);

            // allowScriptableRenderPipeline = true is REQUIRED for URP materials —
            // the default false path renders them magenta/fallback-error.
            _preview.Render(true);
            return _preview.EndPreview();
        }

        /// <summary>
        /// Re-fits the framing distance to <paramref name="mesh"/>'s bounds and re-centers
        /// the camera (D-09: framing re-fits on selection change). Resets orbit yaw/pitch.
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

            // Frame both side-by-side instances: the combined half-width is the
            // horizontal separation plus the mesh radius.
            float halfFov = FieldOfView * 0.5f * Mathf.Deg2Rad;
            float distance = (HalfSeparation + radius) / Mathf.Tan(halfFov);
            _distance = Mathf.Clamp(distance, MinCameraDistance, MaxCameraDistance);
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
            ApplyCamera();
        }

        /// <summary>
        /// Zooms the fixed camera along its viewing axis (positive scroll = closer),
        /// clamped to a sane distance range.
        /// </summary>
        public void Zoom(float scrollDelta)
        {
            _distance = Mathf.Clamp(_distance * (1f - scrollDelta * ZoomSensitivity), MinCameraDistance, MaxCameraDistance);
            ApplyCamera();
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
            // (world origin); orbit rotation is applied to the mesh instances in Render.
            _preview.camera.transform.position = new Vector3(0f, 0f, -_distance);
            _preview.camera.transform.rotation = Quaternion.identity;
        }
    }
}
