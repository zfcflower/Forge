using System.Runtime.CompilerServices;

// Debug-only, like Forge.Scripting, Forge.Approval and (for its non-terminal internals) Forge.HostClient: the
// compilation-cache eviction tests observe the catalog's cache on an in-process host and are compiled only in Debug
// (see CompilationCacheEvictionTests), so a Release build never exposes internals to the test assembly.
#if DEBUG
[assembly: InternalsVisibleTo("Forge.Integration.Tests")]
#endif
