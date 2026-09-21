using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Pins the pool's survival across editor asset sweeps: Unity destroys dynamically
    /// created objects that are not <see cref="HideFlags.DontSave"/> during
    /// <c>AssetDatabase.Refresh</c> / unused-asset sweeps (which fire on selection changes
    /// and reimports). A pooled <see cref="RenderTexture"/> swept that way reads as
    /// Unity-null (<c>rt == null</c>) on later leases, which produced the editor-window
    /// failures ("first selection", stage-toggle recomputes) traced to
    /// NamerAOPipeline.Extract handing SetTexture a destroyed target.
    /// </summary>
    public class ComputeTexturePoolTests
    {
        [Test]
        public void Lease_AfterUnusedAssetSweep_ReturnsLivePooledTarget()
        {
            ComputeTexturePool pool = new ComputeTexturePool();
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(64, 64,
                GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true,
            };

            RenderTexture first = pool.Lease(descriptor);
            pool.Release(first);

            // Simulates the editor's periodic sweep that destroyed the pooled targets.
            EditorUtility.UnloadUnusedAssetsImmediate();

            RenderTexture second = pool.Lease(descriptor);
            try
            {
                Assert.IsFalse(second == null,
                    "a pooled target must survive editor unused-asset sweeps — a swept RT reads as Unity-null and later leases hand out corpses");
                Assert.AreSame(first, second,
                    "the pooled instance must be reused (not swept, not reallocated) across the sweep");
            }
            finally
            {
                pool.Dispose();
            }
        }
    }
}
