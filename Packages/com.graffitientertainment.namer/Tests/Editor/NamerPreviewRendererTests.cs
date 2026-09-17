using System.Collections;
using System.Collections.Generic;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Pins the 04.2-05 preview-presentation rework of <see cref="NamerPreviewRenderer"/>:
    /// one orthographic camera for both panes, one shared yaw+pitch rotation (pitch clamped
    /// to [-89, 89]), zoom as orthographic size (camera transform never moves), and a
    /// rotation-invariant pair-plus-gap initial size. The render-path test is
    /// graphics-capability-gated (D-15); the framing/zoom/orbit tests are pure logic.
    /// </summary>
    public class NamerPreviewRendererTests
    {
        private const float ZoomSensitivity = 0.15f;
        private const float MinZoomScale = 0.05f;
        private const float MaxZoomScale = 10f;
        private const float MinPitch = -89f;
        private const float MaxPitch = 89f;

        private NamerPreviewRenderer _renderer;
        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _renderer = new NamerPreviewRenderer();
        }

        [TearDown]
        public void TearDown()
        {
            if (_renderer != null)
            {
                _renderer.Dispose();
                _renderer = null;
            }

            for (int i = _cleanup.Count - 1; i >= 0; i--)
            {
                if (_cleanup[i] != null)
                {
                    Object.DestroyImmediate(_cleanup[i]);
                }
            }

            _cleanup.Clear();
        }

        [Test]
        public void Construction_PreviewCameraIsOrthographic()
        {
            Assert.IsNotNull(_renderer, "renderer must construct");
            Assert.IsTrue(_renderer.IsOrthographic, "the preview camera must be orthographic from construction");
        }

        [Test]
        public void Orbit_AccumulatesYawAndPitch_ClampsPitch_ResetsOnFrame()
        {
            _renderer.Orbit(10f, 5f);
            _renderer.Orbit(-25f, 3f);

            Assert.AreEqual(-15f, _renderer.YawDegrees, 1e-4f, "yaw accumulates unclamped");
            Assert.AreEqual(8f, _renderer.PitchDegrees, 1e-4f, "pitch accumulates within the clamp");

            _renderer.Orbit(0f, 200f);
            Assert.AreEqual(MaxPitch, _renderer.PitchDegrees, 1e-4f, "pitch clamps at +89");

            _renderer.Orbit(0f, -200f);
            Assert.AreEqual(MinPitch, _renderer.PitchDegrees, 1e-4f, "pitch clamps at -89");

            Mesh mesh = CreateBoundsMesh(1f, 2f, 0.5f);
            _renderer.Frame(mesh);
            Assert.AreEqual(0f, _renderer.YawDegrees, 1e-4f, "Frame resets yaw");
            Assert.AreEqual(0f, _renderer.PitchDegrees, 1e-4f, "Frame resets pitch");

            // Orbit(float, float) is the sole rotation API — a Rotate(float) overload is
            // pinned at compile time by its absence (no such member exists on the type).
        }

        [Test]
        public void Zoom_ClampedScaleFactor_SameWheelDirection()
        {
            _renderer.Zoom(1f);
            Assert.AreEqual(1f - ZoomSensitivity, _renderer.ZoomScale, 1e-4f,
                "positive scroll (zoom in) must shrink the ortho size by ZoomSensitivity");

            _renderer.Zoom(-1f);
            Assert.AreEqual(1f + ZoomSensitivity, _renderer.ZoomScale, 1e-4f,
                "negative scroll (zoom out) must grow the ortho size by ZoomSensitivity");

            for (int i = 0; i < 20; i++)
            {
                _renderer.Zoom(10f);
            }

            Assert.AreEqual(MinZoomScale, _renderer.ZoomScale, 1e-4f, "zoom-in must clamp at MinZoomScale");

            for (int i = 0; i < 20; i++)
            {
                _renderer.Zoom(-10f);
            }

            Assert.AreEqual(MaxZoomScale, _renderer.ZoomScale, 1e-4f, "zoom-out must clamp at MaxZoomScale");
        }

        [Test]
        public void Frame_FitsPairPlusGapAtAnyYawPitch()
        {
            // extents (1, 2, 0.5) -> radius sqrt(5.25) ~= 2.29129: the vertical
            // (radius + PreviewGap) bound dominates the horizontal pair-plus-gap / 2.
            Mesh tallMesh = CreateBoundsMesh(1f, 2f, 0.5f);
            _renderer.Frame(tallMesh);
            Assert.AreEqual(2.3913f, _renderer.OrthographicSizeForAspect(2f), 1e-3f,
                "vertical bound (radius + PreviewGap) must dominate at 2:1");

            // extents (4, 0.5, 0.1) -> radius sqrt(16.26) ~= 4.03237: the rotation-invariant
            // sphere bound dominates the horizontal footprint at 2:1 (the yaw-only
            // horizontal bound is retired), and the pair-plus-gap half-width dominates at 1:1.
            Mesh wideMesh = CreateBoundsMesh(4f, 0.5f, 0.1f);
            _renderer.Frame(wideMesh);
            Assert.AreEqual(4.1324f, _renderer.OrthographicSizeForAspect(2f), 1e-3f,
                "rotation-invariant bound must dominate even for wide-flat meshes at 2:1");
            Assert.AreEqual(4.8324f, _renderer.OrthographicSizeForAspect(1f), 1e-3f,
                "square preview uses the pair-plus-gap half-width (HalfSeparation + radius + PreviewGap)");

            // Rotation invariance: Orbit changes only the shared rotation, never the framing.
            float before = _renderer.OrthographicSizeForAspect(2f);
            _renderer.Orbit(45f, 30f);
            Assert.AreEqual(before, _renderer.OrthographicSizeForAspect(2f), 1e-6f,
                "the framing bound must be unbreakable by any yaw+pitch");
        }

        [UnityTest]
        public IEnumerator Render_OrthoCamera_OneSharedRotation_SizeMatchesFit()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("[NAMER] no graphics device — preview render requires a real device (D-15).");
                yield break;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Assert.Ignore("[NAMER] URP Lit shader not found — preview render requires the URP package.");
                yield break;
            }

            Mesh cube = CreateCubeMesh();
            Material material = CreateUrpLitMaterial(shader);

            // Frame first so Render's EnsureFramed sees the same mesh and does not re-frame
            // (which would reset the rotation); this isolates the claim that Render consumes
            // the one shared rotation without mutating it.
            _renderer.Frame(cube);
            _renderer.Orbit(30f, 20f);

            Texture texture = _renderer.Render(cube, cube, material, material, new Rect(0, 0, 512, 256));

            Assert.IsNotNull(texture, "Render must return a non-null preview texture");
            Assert.IsTrue(_renderer.IsOrthographic, "the camera must stay orthographic across the render");
            Assert.AreEqual(
                _renderer.OrthographicSizeForAspect(2f),
                _renderer.OrthographicSize,
                1e-4f,
                "Render must apply the rect-aspect fit (512/256 = 2) as the camera orthographic size");
            Assert.AreEqual(30f, _renderer.YawDegrees, 1e-4f, "Render must not mutate yaw");
            Assert.AreEqual(20f, _renderer.PitchDegrees, 1e-4f, "Render must not mutate pitch");

            yield return null;
        }

        private Mesh CreateBoundsMesh(float halfX, float halfY, float halfZ)
        {
            Mesh mesh = new Mesh
            {
                name = "NamerPreviewBoundsMesh",
                hideFlags = HideFlags.HideAndDontSave,
                bounds = new Bounds(Vector3.zero, new Vector3(halfX * 2f, halfY * 2f, halfZ * 2f)),
            };
            _cleanup.Add(mesh);
            return mesh;
        }

        private Mesh CreateCubeMesh()
        {
            Mesh mesh = new Mesh { name = "NamerPreviewCube", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                4, 7, 3, 4, 3, 0,
                5, 1, 2, 5, 2, 6,
                7, 6, 2, 7, 2, 3,
                0, 1, 5, 0, 5, 4,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _cleanup.Add(mesh);
            return mesh;
        }

        private Material CreateUrpLitMaterial(Shader shader)
        {
            Material material = new Material(shader)
            {
                name = "NamerPreviewUrpLit",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _cleanup.Add(material);
            return material;
        }
    }
}
