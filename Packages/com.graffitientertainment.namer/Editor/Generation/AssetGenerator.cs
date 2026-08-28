using System;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// The sole disk writer for the NAMER pipeline (GEN-01, D-01/D-02/D-04/D-06/D-08).
    /// Turns an in-memory <see cref="NamerComputeResult"/> (pool-leased render targets)
    /// plus a <see cref="NamerMaterialInspection"/> into persistent, correctly-imported
    /// NAMER assets under the configured destination folder:
    ///   - readback both render targets via the pipeline's <see cref="AsyncGPUReadback"/>
    ///     contract (no synchronous full-texture reads on 2K/4K outputs),
    ///   - write the packed surface PNG (linear, uncompressed, point, no mips),
    ///   - write the base-color PNG (linear-&gt;sRGB GPU conversion, then sRGB import),
    ///   - create the NAMER material carrying the full D-07 metadata contract,
    ///   - stamp every generated asset with the <c>NamerGenerated</c> label.
    ///
    /// This is the ONLY type that calls <c>File.WriteAllBytes</c> and
    /// <c>AssetDatabase.CreateAsset</c>; source paths are never passed to any write API,
    /// and source importers are never mutated (only the newly generated files are stamped).
    /// </summary>
    public sealed class AssetGenerator
    {
        /// <summary>
        /// Generates a material + two textures for a single material inspection and returns
        /// their written paths. Throws <see cref="InvalidOperationException"/> on a
        /// non-stamped overwrite target, a failed readback, an escaping destination, or a
        /// missing NAMER shader — never writes garbage to disk.
        /// </summary>
        public NamerGeneratedAsset Generate(
            NamerComputeResult result,
            NamerMaterialInspection inspection,
            NamerProcessorSettings settings,
            string destinationFolder)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (inspection == null)
            {
                throw new ArgumentNullException(nameof(inspection));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            ValidateDestinationFolder(destinationFolder);
            ValidatePrefixAndSuffix(settings);

            string safeName = SanitizeFileName(inspection.Material != null ? inspection.Material.name : "Unnamed");
            string prefix = settings.Prefix ?? string.Empty;
            string suffix = settings.Suffix ?? string.Empty;

            string basePath = destinationFolder + prefix + safeName + suffix + "_Base.png";
            string surfacePath = destinationFolder + prefix + safeName + suffix + "_Surface.png";
            string materialPath = destinationFolder + prefix + safeName + suffix + ".mat";

            // T-03-01 defense in depth: re-validate the fully composed paths, not just
            // the destination folder, so no future change can escape the configured folder.
            ValidateComposedPath(basePath, destinationFolder);
            ValidateComposedPath(surfacePath, destinationFolder);
            ValidateComposedPath(materialPath, destinationFolder);

            int width = result.Width;
            int height = result.Height;

            // D-08: read back via the pipeline's AsyncGPUReadback contract. Both RTs are
            // RGBA32-quantized on readback; forcePlayerLoopUpdate pumps the request even
            // in edit mode where there is no player loop driving async GPU reads.
            AsyncGPUReadbackRequest surfaceRequest =
                NamerComputePipeline.RequestReadback(result.PackedSurface, 0, TextureFormat.RGBA32);
            AsyncGPUReadbackRequest baseRequest =
                NamerComputePipeline.RequestReadback(result.NormalizedBaseColor, 0, TextureFormat.RGBA32);

            surfaceRequest.forcePlayerLoopUpdate = true;
            baseRequest.forcePlayerLoopUpdate = true;

            surfaceRequest.WaitForCompletion();
            baseRequest.WaitForCompletion();

            if (surfaceRequest.hasError || baseRequest.hasError)
            {
                throw new InvalidOperationException(
                    "GPU readback failed while generating NAMER assets; no files were written.");
            }

            NativeArray<byte> surfaceData = surfaceRequest.GetData<byte>();
            NativeArray<byte> baseData = baseRequest.GetData<byte>();

            // Packed surface: linear data (R8G8B8A8_UNorm), loaded raw with no color-space
            // conversion. Bit-packed alpha cannot survive any conversion, compression,
            // mip, or interpolated sampling.
            Texture2D surfaceTex = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            surfaceTex.LoadRawTextureData(surfaceData);
            surfaceTex.Apply(false, false);

            // Base color: read back into a LINEAR texture, then convert linear -> sRGB on
            // the GPU via Graphics.ConvertTexture (the exact inverse of the compute shader's
            // SRGBToLinear, IEC 61966-2-1). No per-pixel C# loop (NORM-03) and no custom
            // shader/compute asset.
            Texture2D baseLinear = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            baseLinear.LoadRawTextureData(baseData);
            baseLinear.Apply(false, false);

            // Destination texture is sRGB (graphicsFormat == GraphicsFormat.R8G8B8A8_SRGB):
            // a Texture2D constructed with linear=false uses the sRGB variant of RGBA32.
            Texture2D baseSrgb = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            Graphics.ConvertTexture(baseLinear, baseSrgb);

            try
            {
                WriteSurfaceTexture(surfaceTex, surfacePath, settings.OverwriteGenerated);
                WriteBaseTexture(baseSrgb, basePath, settings.OverwriteGenerated);
                WriteMaterial(inspection, surfacePath, basePath, materialPath, settings.OverwriteGenerated);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(surfaceTex);
                UnityEngine.Object.DestroyImmediate(baseLinear);
                UnityEngine.Object.DestroyImmediate(baseSrgb);
            }

            return new NamerGeneratedAsset
            {
                MaterialPath = materialPath,
                BaseTexturePath = basePath,
                SurfaceTexturePath = surfacePath,
            };
        }

        /// <summary>
        /// The single shared file-name sanitizer (D-02, T-03-01). Strips path separators,
        /// the colon, and <see cref="Path.GetInvalidFileNameChars"/>, trims, and falls back
        /// to <c>"Unnamed"</c> when nothing valid remains. <see cref="NamerProcessor"/>
        /// consumes this same method for the per-selection subfolder name.
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "Unnamed";
            }

            char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '/' || c == '\\' || c == ':' || Array.IndexOf(invalidFileNameChars, c) >= 0)
                {
                    continue;
                }

                builder.Append(c);
            }

            string result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "Unnamed" : result;
        }

        // ---------------------------------------------------------------------
        // Destination confinement (T-03-01)
        // ---------------------------------------------------------------------

        private static void ValidateDestinationFolder(string destinationFolder)
        {
            if (string.IsNullOrEmpty(destinationFolder))
            {
                throw new InvalidOperationException("Destination folder must not be empty.");
            }

            string fullDestination = Path.GetFullPath(destinationFolder);
            string assetsFullPath = Path.GetFullPath(Application.dataPath);

            if (!fullDestination.Equals(assetsFullPath, StringComparison.OrdinalIgnoreCase)
                && !fullDestination.StartsWith(assetsFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Destination '" + destinationFolder + "' must resolve under the project Assets/ folder.");
            }
        }

        // ---------------------------------------------------------------------
        // Prefix/suffix + composed-path confinement (T-03-01)
        // ---------------------------------------------------------------------

        private static void ValidatePrefixAndSuffix(NamerProcessorSettings settings)
        {
            ValidatePathSegment(settings.Prefix ?? string.Empty, "Prefix");
            ValidatePathSegment(settings.Suffix ?? string.Empty, "Suffix");
        }

        private static void ValidatePathSegment(string value, string fieldName)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (value.IndexOf("..", StringComparison.Ordinal) >= 0
                || value.IndexOf('/') >= 0
                || value.IndexOf('\\') >= 0
                || value.IndexOf(':') >= 0)
            {
                throw new InvalidOperationException(
                    fieldName + " must not contain '..' or path separators ('/', '\\', ':').");
            }
        }

        private static void ValidateComposedPath(string path, string destinationFolder)
        {
            string fullPath = Path.GetFullPath(path);
            string fullDestination = Path.GetFullPath(destinationFolder.TrimEnd('/', '\\'));

            if (!fullPath.Equals(fullDestination, StringComparison.OrdinalIgnoreCase)
                && !fullPath.StartsWith(fullDestination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Generated path escapes the destination folder: '" + path + "'.");
            }
        }

        // ---------------------------------------------------------------------
        // Texture writes (Pattern 2: import-then-stamp)
        // ---------------------------------------------------------------------

        private static void WriteSurfaceTexture(Texture2D texture, string path, bool overwriteGenerated)
        {
            EnsureWritableTarget(path, overwriteGenerated);

            byte[] png = ImageConversion.EncodeToPNG(texture);
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path);

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("No TextureImporter found for generated surface texture '" + path + "'.");
            }

            // GEN-04: packed surface must be linear, uncompressed, point-filtered, no mips.
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();

            Stamp(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
        }

        private static void WriteBaseTexture(Texture2D texture, string path, bool overwriteGenerated)
        {
            EnsureWritableTarget(path, overwriteGenerated);

            byte[] png = ImageConversion.EncodeToPNG(texture);
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path);

            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("No TextureImporter found for generated base texture '" + path + "'.");
            }

            // D-06: base color imports sRGB; leave compression/filter/mips at color defaults.
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.SaveAndReimport();

            Stamp(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
        }

        // ---------------------------------------------------------------------
        // Material write (D-07 metadata contract)
        // ---------------------------------------------------------------------

        private static void WriteMaterial(
            NamerMaterialInspection inspection,
            string surfacePath,
            string basePath,
            string materialPath,
            bool overwriteGenerated)
        {
            EnsureWritableTarget(materialPath, overwriteGenerated);

            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Shader 'GraffitiEntertainment.Namer/NAMER' was not found. Ensure the NAMER shader compiled and imported.");
            }

            Material material = new Material(shader);
            material.name = Path.GetFileNameWithoutExtension(materialPath);

            material.SetTexture("_SurfaceMap", AssetDatabase.LoadAssetAtPath<Texture2D>(surfacePath));
            material.SetTexture("_BaseResidualMap", AssetDatabase.LoadAssetAtPath<Texture2D>(basePath));
            material.SetColor("_BaseColor", inspection.BaseColor);
            material.SetColor("_EmissionColor", inspection.EmissionColor);
            material.SetFloat("_OcclusionStrength", inspection.OcclusionStrength);
            material.SetFloat("_Cutoff", inspection.Cutoff);

            if (inspection.EmissionMap != null || IsNonBlack(inspection.EmissionColor))
            {
                material.SetFloat("_EMISSION", 1f);
                material.EnableKeyword("_EMISSION");
            }

            if (IsAlphaTested(inspection))
            {
                material.SetFloat("_ALPHATEST_ON", 1f);
                material.EnableKeyword("_ALPHATEST_ON");
            }

            if (inspection.SurfaceType > 0f)
            {
                material.SetFloat("_Surface", 1f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
            }

            AssetDatabase.CreateAsset(material, materialPath);
            Stamp(AssetDatabase.LoadAssetAtPath<Material>(materialPath));
        }

        /// <summary>
        /// Detects alpha-testing on the source material: URP Lit exposes the
        /// <c>_AlphaClip</c> toggle, Standard uses <c>_Mode == 1</c> (cutout).
        /// </summary>
        private static bool IsAlphaTested(NamerMaterialInspection inspection)
        {
            Material source = inspection.Material;
            if (source == null)
            {
                return false;
            }

            if (inspection.IsUrpLit && source.HasProperty("_AlphaClip"))
            {
                return source.GetFloat("_AlphaClip") > 0f;
            }

            if (inspection.IsStandard && source.HasProperty("_Mode"))
            {
                return Mathf.Approximately(source.GetFloat("_Mode"), 1f);
            }

            return false;
        }

        private static bool IsNonBlack(Color color)
        {
            return color.r > 0f || color.g > 0f || color.b > 0f;
        }

        // ---------------------------------------------------------------------
        // Overwrite gate + label stamping (D-04, T-03-02)
        // ---------------------------------------------------------------------

        private static void EnsureWritableTarget(string path, bool overwriteGenerated)
        {
            UnityEngine.Object existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (existing == null)
            {
                return;
            }

            bool stamped = Array.IndexOf(AssetDatabase.GetLabels(existing), NamerEditorConstants.GeneratedLabel) >= 0;
            if (!stamped || !overwriteGenerated)
            {
                throw new InvalidOperationException(
                    "Refusing to overwrite non-generated asset '" + path + "'. "
                    + "Only NamerGenerated-stamped assets in the destination folder can be overwritten.");
            }
        }

        private static void Stamp(UnityEngine.Object asset)
        {
            if (asset == null)
            {
                return;
            }

            AssetDatabase.SetLabels(asset, new[] { NamerEditorConstants.GeneratedLabel });
        }
    }

    /// <summary>
    /// Written paths for one generated NAMER asset set (material + base + surface textures).
    /// </summary>
    public sealed class NamerGeneratedAsset
    {
        public string MaterialPath;
        public string BaseTexturePath;
        public string SurfaceTexturePath;
    }
}
