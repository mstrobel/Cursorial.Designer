using Xunit;

// The provider-hijack regression test save/restores the process-global
// XamlLoaderOptions.DefaultMetadataProvider, which parallel test classes would observe mid-load
// (in production the host captures the default once, at process start, before any user assembly
// loads — a race no real session has). Serialized for determinism; the suite is small and fast.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
