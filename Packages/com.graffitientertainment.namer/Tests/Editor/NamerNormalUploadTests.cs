using System.Collections.Generic;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Pins the normal-map upload path against Unity's platform normal-map layouts
    /// AND green-channel convention. A source normal map imported as
    /// <c>TextureImporterType.NormalMap</c> does not keep plain RGB on the GPU:
    /// desktop DXT5 stores the swizzled layout (1, y, 1, x), BC5 stores
    /// (x, y, 0, 1), and only an uncompressed/plain import keeps (x, y, z, 1).
    /// Independently, whatever an importer stores is in Unity's canonical
    /// Y+ green convention (URP's UnpackNormal* in Packing.hlsl applies no flip:
    /// n.y = 2g - 1), while the NAMER format's texel is DirectX-style — its decode
    /// un-flips green (n.y = 1 - 2g). The upload must therefore normalize both:
    /// x = r * a recovers X in every layout (the same trick UnpackNormalMapRGorAG
    /// uses), z is reconstructed on the unit shell, and green is flipped so the
    /// decode's un-flip returns Unity's canonical n.y and the after pane matches
    /// the URP before pane. Rendering the raw texel fed the encode R pinned to 1
    /// with the real X stranded in alpha; passing green through unflipped mirrored
    /// the tangent Y (inverted bumps on part of the triangles).
    /// </summary>
    public class NamerNormalUploadTests
    {
        /// <summary>Canonical Unity-convention texels on the unit shell (2r-1)^2+(2g-1)^2+(2b-1)^2 = 1.</summary>
        private static readonly Vector3[] AuthoredTexels =
        {
            new Vector3(0.50f, 0.50f, 1.0000f),  // neutral +Z
            new Vector3(0.80f, 0.35f, 0.8708f),  // +X lean   (dx= 0.6, dy=-0.3)
            new Vector3(0.15f, 0.75f, 0.7550f),  // -X lean   (dx=-0.7, dy= 0.5)
            new Vector3(0.30f, 0.10f, 0.7236f),  // -X/-Y lean(dx=-0.4, dy=-0.8)
        };

        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
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
        public void Upload_UnpackNormal_ReconstructsAuthoredTexel_FromEveryGpuLayout()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("[NAMER] no graphics device — the unpack blit requires a real device (D-15).");
                return;
            }

            // One source per Unity GPU layout, each encoding the same authored texels.
            Texture2D plain = CreateLayoutSource((x, y, z) => new Color32(
                ToByte(x), ToByte(y), ToByte(z), 255));
            Texture2D dxt5nm = CreateLayoutSource((x, y, z) => new Color32(
                255, ToByte(y), 255, ToByte(x)));
            Texture2D bc5 = CreateLayoutSource((x, y, z) => new Color32(
                ToByte(x), ToByte(y), 0, 255));

            AssertTexelsRoundTrip(plain, "plain RGBA (x, y, z, 1)");
            AssertTexelsRoundTrip(dxt5nm, "DXT5nm swizzle (1, y, 1, x)");
            AssertTexelsRoundTrip(bc5, "BC5 swizzle (x, y, 0, 1)");
        }

        /// <summary>
        /// Control for the bug being fixed: the raw upload path (base color, AO,
        /// metallic sources) must stay a byte-exact raw copy — the normal-layout
        /// reconstruction lives only behind the unpackNormal flag.
        /// </summary>
        [Test]
        public void Upload_Raw_StaysRawCopy_ForSwizzledSource()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("[NAMER] no graphics device — the raw blit requires a real device (D-15).");
                return;
            }

            Texture2D dxt5nm = CreateLayoutSource((x, y, z) => new Color32(
                255, ToByte(y), 255, ToByte(x)));
            Color[] read = UploadAndRead(dxt5nm, unpackNormal: false);

            for (int i = 0; i < AuthoredTexels.Length; i++)
            {
                Vector3 authored = AuthoredTexels[i];
                Color got = read[ProbeIndex(i)];

                Assert.AreEqual(1f, got.r, 1f / 255f,
                    "raw upload must copy the swizzled R=1 verbatim");
                Assert.AreEqual(authored.y, got.g, 1f / 255f,
                    "raw upload must copy G verbatim");
                Assert.AreEqual(authored.x, got.a, 1f / 255f,
                    "raw upload must keep the normal X in alpha verbatim");
            }
        }

        private void AssertTexelsRoundTrip(Texture2D source, string layoutName)
        {
            Color[] read = UploadAndRead(source, unpackNormal: true);

            for (int i = 0; i < AuthoredTexels.Length; i++)
            {
                Vector3 authored = AuthoredTexels[i];
                Color got = read[ProbeIndex(i)];

                Assert.AreEqual(authored.x, got.r, 3f / 255f,
                    layoutName + ": reconstructed texel R must be the authored normal X");
                Assert.AreEqual(1f - authored.y, got.g, 3f / 255f,
                    layoutName + ": reconstructed texel G must be the DirectX-flipped authored normal Y (the decode's 1-2g un-flip then returns Unity's canonical n.y)");
                Assert.AreEqual(authored.z, got.b, 3f / 255f,
                    layoutName + ": reconstructed texel B must be the authored normal Z");
            }
        }

        private Color[] UploadAndRead(Texture2D source, bool unpackNormal)
        {
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(8, 8,
                GraphicsFormat.R8G8B8A8_UNorm, 0);
            RenderTexture target = new RenderTexture(descriptor)
            {
                name = "NamerNormalUploadTarget",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _cleanup.Add(target);

            NamerComputePipeline.Upload(source, target, null, unpackNormal);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D read = new Texture2D(8, 8, TextureFormat.RGBA32, false, true)
            {
                name = "NamerNormalUploadReadback",
                hideFlags = HideFlags.HideAndDontSave,
            };
            read.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
            read.Apply();
            RenderTexture.active = prev;

            Color[] pixels = read.GetPixels();
            Object.DestroyImmediate(read);
            return pixels;
        }

        /// <summary>An 8x8 source: authored texel i at pixel (i+1, i+1), neutral elsewhere.</summary>
        private Texture2D CreateLayoutSource(System.Func<float, float, float, Color32> layout)
        {
            Texture2D texture = new Texture2D(8, 8, TextureFormat.RGBA32, false, true)
            {
                name = "NamerNormalUploadSource",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
            };
            _cleanup.Add(texture);

            Color32 neutral = layout(0.5f, 0.5f, 1f);
            Color32[] pixels = new Color32[8 * 8];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = neutral;
            }

            for (int i = 0; i < AuthoredTexels.Length; i++)
            {
                Vector3 texel = AuthoredTexels[i];
                pixels[ProbeIndex(i)] = layout(texel.x, texel.y, texel.z);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static int ProbeIndex(int i)
        {
            return (i + 1) * 8 + (i + 1);
        }

        private static byte ToByte(float v)
        {
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);
        }
    }
}
