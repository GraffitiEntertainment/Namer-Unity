// TEMPORARY diagnostic harness (04.2 residual-smeared-reconstruction investigation).
// Not part of the suite — delete after the repro run. Pins the exact projection-path
// settings and reports the NamerDecompErrorStats fields (FitOnlyMaxError, ChosenResolution,
// AvgError/MaxError/Coverage) plus the produced residual RT dimensions, so the adaptive
// resolution ladder's 128 choice for a 2048 source can be attributed to either the
// settings (hypothesis a) or a metric bug (hypothesis b). Mirrors NamerProcessor.Process's
// RemovedDetail projection wiring (CreateProjectionContext + pipeline.Process).
using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor.Tests
{
    public class NamerDipSwitchReproTests
    {
        private const string MarkerPath = "Temp/namer-residual-reso-repro.txt";
        private const string SourceFbx = "Assets/Models/Neo/Neo-T-Pose.fbx";
        private const string SourceMaterial = "Assets/Models/Neo/tripo_mat_d83278e6.mat";
        private const int ResidualRes = 0; // auto (window default)

        private static void Log(string line)
        {
            File.AppendAllText(MarkerPath, line + "\n");
        }

        [Test]
        public void PinnedProjectionPath_ReportsChosenResolution_AtTwoThresholds()
        {
            File.WriteAllText(MarkerPath, "=== residual-resolution repro " + DateTime.Now.ToString("o") + "\n");

            // The FBX imports with EXTERNAL materials (no Material sub-asset — verified:
            // 124 sub-assets, none Material), so Inspect(fbxRoot) collects nothing. The
            // 17:13 run's inspection unit is the external .mat (NamerSource tag resolves
            // to d0691ba7e72264bfe9112baabba16338|2100000 = this file). Inspect it directly.
            Material sourceMat = AssetDatabase.LoadAssetAtPath<Material>(SourceMaterial);
            Assert.IsNotNull(sourceMat, "source material loaded");
            NamerSourceModel model = SourceInspector.Inspect(sourceMat);
            Assert.AreEqual(1, model.Materials.Count, "single source material");
            NamerMaterialInspection inspection = model.Materials[0];

            Mesh mesh = null;
            foreach (UnityEngine.Object sub in AssetDatabase.LoadAllAssetsAtPath(SourceFbx))
            {
                if (sub is Mesh m)
                {
                    mesh = m;
                    break;
                }
            }

            Assert.IsNotNull(mesh, "mesh sub-asset resolved");
            Log("baseMap=" + (inspection.BaseMap != null ? inspection.BaseMap.width + "x" + inspection.BaseMap.height : "null")
                + " mesh=" + mesh.name + " vc=" + mesh.vertexCount);

            // The 17:13 run's LIVE EditorPrefs (com.unity3d.UnityEditor5.x.plist):
            //   NamerProcessor.ErrorThreshold = 0.06 (NOT the 0.02 default)
            //   NamerProcessor.ResidualResolution = 0 (Auto)
            //   NamerProcessor.DecompositionEnabled = 1
            //   NamerProcessor.RoughnessExtractStrength = 0.0778
            // So bracket both thresholds: the pinned default (0.02) and the observed live (0.06).
            foreach (float threshold in new[] { 0.02f, 0.06f })
            {
                RunPinnedRepro(inspection, mesh, threshold);
            }

            Log("=== FINISHED residual-resolution repro");
        }

        private static void RunPinnedRepro(NamerMaterialInspection inspection, Mesh mesh, float threshold)
        {
            // Window-tweaked fields (declared so the run is interpretable). Pin the
            // projection path exactly: RemovedDetail dip source, writeResidual forced on.
            inspection.RoughnessExtractStrength = 0.25f;
            inspection.BakeSourceMesh = mesh;
            inspection.DipSource = NamerDipSource.RemovedDetail;

            MethodInfo createCtx = typeof(NamerProcessor).GetMethod(
                "CreateProjectionContext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(createCtx, "CreateProjectionContext located");

            NamerComputePipeline pipeline = new NamerComputePipeline();
            NamerDecompPipeline decompPipeline = new NamerDecompPipeline();
            try
            {
                NamerSplitResult fitSplit = MeshVertexSplitter.Split(mesh);
                int w = inspection.BaseMap.width;
                int h = inspection.BaseMap.height;
                // Trailing arg = the pinned 0.99 default (D-05) — matches the post-04.3-02 parameter order.
                NamerProjectionContext ctx = (NamerProjectionContext)createCtx.Invoke(
                    null, new object[] { fitSplit, decompPipeline, w, h, threshold, ResidualRes, true,
                        NamerEditorConstants.DefaultCoverageTarget });

                NamerComputeResult r = pipeline.Process(inspection, ctx);
                try
                {
                    if (ctx.Decomp == null || ctx.Decomp.Stats == null)
                    {
                        Log("threshold=" + threshold + " decomp=null (no residual produced)");
                        return;
                    }

                    NamerDecompErrorStats s = ctx.Decomp.Stats;
                    int rw = ctx.Decomp.Residual != null ? ctx.Decomp.Residual.width : -1;
                    int rh = ctx.Decomp.Residual != null ? ctx.Decomp.Residual.height : -1;
                    Log("threshold=" + threshold
                        + " fitOnlyMaxError=" + s.FitOnlyMaxError.ToString("F6")
                        + " chosenResolution=" + s.ChosenResolution
                        + " residualDims=" + rw + "x" + rh
                        + " avgError=" + s.AvgError.ToString("F6")
                        + " maxError=" + s.MaxError.ToString("F6")
                        + " coverage=" + s.Coverage.ToString("F6")
                        + " residualRequired=" + s.ResidualRequired
                        + " cannotDecompose=" + s.CannotDecompose);
                }
                finally
                {
                    pipeline.ReleaseResult(r);
                    ctx.Decomp?.Dispose();
                }
            }
            finally
            {
                decompPipeline.Dispose();
                pipeline.Dispose();
            }
        }
    }
}
