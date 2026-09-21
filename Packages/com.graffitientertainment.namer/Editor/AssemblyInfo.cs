using System.Runtime.CompilerServices;

// EditMode tests verify pipeline internals (e.g. NamerComputePipeline.Upload's
// raw vs normal-unpack blit paths) without widening the package's public API.
[assembly: InternalsVisibleTo("GraffitiEntertainment.Namer.Tests.Editor")]
