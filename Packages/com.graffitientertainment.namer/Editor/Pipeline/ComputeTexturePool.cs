using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Sole <see cref="RenderTexture"/> allocator for the NAMER compute pipeline
    /// (D-12, T-02-04). Leases reusable render targets by <see cref="RenderTextureDescriptor"/>
    /// and tracks every live allocation so 02-03's leak watchdog can assert
    /// <see cref="LiveCount"/> returns to baseline after a batch.
    ///
    /// No <see cref="RenderTexture"/> may be constructed outside this class; the pipeline
    /// calls <see cref="Lease"/> / <see cref="Release"/> and never builds RTs directly.
    /// </summary>
    public sealed class ComputeTexturePool : IDisposable
    {
        private readonly Dictionary<RenderTextureDescriptor, Stack<RenderTexture>> _pool =
            new Dictionary<RenderTextureDescriptor, Stack<RenderTexture>>();

        // Remembers the descriptor each leased RT was created from, so Release can push
        // it back into the correct stack without relying on rt.descriptor round-trip
        // normalization (which may differ from the descriptor we passed in).
        private readonly Dictionary<RenderTexture, RenderTextureDescriptor> _leasedDescriptor =
            new Dictionary<RenderTexture, RenderTextureDescriptor>();

        private readonly HashSet<RenderTexture> _live = new HashSet<RenderTexture>();

        /// <summary>Number of render targets currently leased and not yet returned.</summary>
        public int LiveCount => _live.Count;

        /// <summary>
        /// Returns a pooled render target matching <paramref name="descriptor"/> if one is
        /// available, otherwise allocates a new one. The returned target is tracked as live.
        /// </summary>
        public RenderTexture Lease(RenderTextureDescriptor descriptor)
        {
            if (_pool.TryGetValue(descriptor, out Stack<RenderTexture> stack) && stack.Count > 0)
            {
                RenderTexture reused = stack.Pop();
                _live.Add(reused);
                _leasedDescriptor[reused] = descriptor;
                return reused;
            }

            RenderTexture created = new RenderTexture(descriptor)
            {
                name = "NamerComputeRT",
            };
            created.Create();
            _live.Add(created);
            _leasedDescriptor[created] = descriptor;
            return created;
        }

        /// <summary>
        /// Returns a leased render target to the pool for reuse by a later
        /// <see cref="Lease"/> call. No-ops for null or already-released targets.
        /// </summary>
        public void Release(RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            if (!_live.Remove(rt))
            {
                return;
            }

            if (!_leasedDescriptor.TryGetValue(rt, out RenderTextureDescriptor descriptor))
            {
                descriptor = rt.descriptor;
            }

            _leasedDescriptor.Remove(rt);

            if (!_pool.TryGetValue(descriptor, out Stack<RenderTexture> stack))
            {
                stack = new Stack<RenderTexture>();
                _pool[descriptor] = stack;
            }

            stack.Push(rt);
        }

        /// <summary>
        /// Releases and destroys every render target the pool has allocated (both live and
        /// pooled). Call once when the owning pipeline is disposed.
        /// </summary>
        public void Dispose()
        {
            foreach (RenderTexture rt in _live)
            {
                DestroyRenderTexture(rt);
            }

            _live.Clear();
            _leasedDescriptor.Clear();

            foreach (Stack<RenderTexture> stack in _pool.Values)
            {
                while (stack.Count > 0)
                {
                    DestroyRenderTexture(stack.Pop());
                }
            }

            _pool.Clear();
        }

        private static void DestroyRenderTexture(RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
