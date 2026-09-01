using System;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Builds and configures the editor-only debug channel material (D-12). Caches the
    /// three debug-shader property IDs once (avoids re-hashing strings per draw) and
    /// centralizes the <c>Shader.Find("GraffitiEntertainment.Namer/NamerDebugView")</c>
    /// null-throw so the preview and any future test share one convention.
    /// </summary>
    public sealed class NamerDebugChannelMaterial
    {
        private static readonly int SurfaceMapId = Shader.PropertyToID("_SurfaceMap");
        private static readonly int BaseResidualMapId = Shader.PropertyToID("_BaseResidualMap");
        private static readonly int DebugBaseMapId = Shader.PropertyToID("_DebugBaseMap");
        private static readonly int DebugChannelId = Shader.PropertyToID("_DebugChannel");

        /// <summary>
        /// Creates a fresh debug material from the NamerDebugView shader, throwing
        /// <see cref="InvalidOperationException"/> if the shader did not compile/import.
        /// </summary>
        public Material Create()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NamerDebugView");
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Shader 'GraffitiEntertainment.Namer/NamerDebugView' was not found. Ensure the NAMER debug shader compiled and imported.");
            }

            return new Material(shader);
        }

        /// <summary>Assigns the generated packed-surface and base-residual textures.</summary>
        public void SetTextures(Material material, Texture surface, Texture baseResidual)
        {
            if (material == null)
            {
                throw new ArgumentNullException(nameof(material));
            }

            material.SetTexture(SurfaceMapId, surface);
            material.SetTexture(BaseResidualMapId, baseResidual);
        }

        /// <summary>
        /// Assigns the debug base map the Error Heatmap channel (8) samples for its
        /// reconstruction-error comparison. Null clears the bind (channel falls back to a
        /// uniform-zero error).
        /// </summary>
        public void SetDebugBaseMap(Material material, Texture baseMap)
        {
            if (material == null)
            {
                throw new ArgumentNullException(nameof(material));
            }

            material.SetTexture(DebugBaseMapId, baseMap);
        }

        /// <summary>Selects the debug channel (0..8 per the UI-SPEC debug channel contract).</summary>
        public void SetChannel(Material material, int channel)
        {
            if (material == null)
            {
                throw new ArgumentNullException(nameof(material));
            }

            material.SetFloat(DebugChannelId, channel);
        }
    }
}
