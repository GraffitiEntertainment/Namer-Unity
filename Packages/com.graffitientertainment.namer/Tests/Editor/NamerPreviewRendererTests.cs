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
    /// Pins the 04.2-09 preview-framing rework of <see cref="NamerPreviewRenderer"/>: one
    /// orthographic camera for both panes, one shared yaw+pitch rotation (pitch clamped to
    /// [-89, 89]), zoom as orthographic size (camera transform never moves), and a
    /// divider-anchored tight neutral-AABB fit (each pane's object hugs the divider with an
    /// <see cref="NamerPreviewRenderer.InnerMarginPx"/> inner margin). The render-path test is
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

            // Zoom accumulates multiplicatively (the retired distance zoom's curve), so the
            // opposite wheel direction is probed from a fresh neutral scale via Frame.
            _renderer.Frame(CreateBoundsMesh(1f, 2f, 0.5f));

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
        public void Frame_FitsTightAabbWithInnerMargin()
        {
            // Tight neutral-AABB fit: extents (1, 2, 0.5) -> the vertical half-extent ey=2
            // dominates (horizFit 400/375 = 1.0667, vertFit 800/370 = 2.1622).
            Mesh tallMesh = CreateBoundsMesh(1f, 2f, 0.5f);
            _renderer.Frame(tallMesh);
            Assert.AreEqual(2.1622f, _renderer.OrthographicSizeForAspect(2f, 400f), 1e-3f,
                "tight AABB: vertical ey=2 dominates (800/370)");

            // extents (4, 0.5, 0.1): the neutral horizontal half-extent ex=4 dominates the
            // wide mesh at 2:1 (horizFit 1600/375); at 1:1 the pane is 200px wide so the
            // horizontal fit dominates harder (1600/175).
            Mesh wideMesh = CreateBoundsMesh(4f, 0.5f, 0.1f);
            _renderer.Frame(wideMesh);
            Assert.AreEqual(4.2667f, _renderer.OrthographicSizeForAspect(2f, 400f), 1e-3f,
                "tight AABB: wide mesh at 2:1 (1600/375)");
            Assert.AreEqual(9.1429f, _renderer.OrthographicSizeForAspect(1f, 400f), 1e-3f,
                "tight AABB horizontal ex=4 in a 200px pane (1600/175)");

            // Fit rotation invariance: the neutral-AABB fit is fixed at Frame — anchoring,
            // not fitting, absorbs rotation.
            float before = _renderer.OrthographicSizeForAspect(2f, 400f);
            _renderer.Orbit(45f, 30f);
            Assert.AreEqual(before, _renderer.OrthographicSizeForAspect(2f, 400f), 1e-6f,
                "the neutral-AABB fit is fixed at Frame — anchoring, not fitting, absorbs rotation");
        }

        [Test]
        public void HorizontalHalfExtentWorld_TracksCurrentRotation()
        {
            Mesh wideMesh = CreateBoundsMesh(4f, 0.5f, 0.1f);
            _renderer.Frame(wideMesh);
            Assert.AreEqual(4f, _renderer.HorizontalHalfExtentWorld(), 1e-4f, "neutral: X extent");

            _renderer.Orbit(90f, 0f);
            Assert.AreEqual(0.1f, _renderer.HorizontalHalfExtentWorld(), 1e-4f,
                "yaw 90: wide axis rotates into depth, X extent = Z extent");

            _renderer.Frame(wideMesh);
            _renderer.Orbit(45f, 0f);
            Assert.AreEqual(2.8991f, _renderer.HorizontalHalfExtentWorld(), 1e-3f,
                "yaw 45: |cos45|*4 + |sin45|*0.1 = 0.70710678*4.1");
        }

        [Test]
        public void PaneAnchor_InnerEdgeStaysAtInnerMarginAcrossZoomSweep()
        {
            Mesh wideMesh = CreateBoundsMesh(4f, 0.5f, 0.1f);
            _renderer.Frame(wideMesh);

            for (int i = 0; i < 24; i++)
            {
                float ortho = _renderer.OrthographicSizeForAspect(2f, 400f);
                float anchor = _renderer.PaneAnchorWorld(2f, 400f);
                float xExtent = _renderer.HorizontalHalfExtentWorld();
                float innerEdgePx = (anchor - xExtent) * (400f / (2f * ortho));
                Assert.AreEqual(NamerPreviewRenderer.InnerMarginPx, innerEdgePx, 1e-3f,
                    "inner edge must stay InnerMarginPx from the divider at every zoom");
                _renderer.Zoom(1f);
            }

            _renderer.Frame(wideMesh);
            _renderer.Orbit(35f, 20f);
            for (int i = 0; i < 24; i++)
            {
                float ortho = _renderer.OrthographicSizeForAspect(2f, 400f);
                float anchor = _renderer.PaneAnchorWorld(2f, 400f);
                float xExtent = _renderer.HorizontalHalfExtentWorld();
                float innerEdgePx = (anchor - xExtent) * (400f / (2f * ortho));
                Assert.AreEqual(NamerPreviewRenderer.InnerMarginPx, innerEdgePx, 1e-3f,
                    "inner edge must stay InnerMarginPx from the divider at every zoom, even rotated");
                _renderer.Zoom(1f);
            }
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
                _renderer.OrthographicSizeForAspect(2f, 256f),
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
