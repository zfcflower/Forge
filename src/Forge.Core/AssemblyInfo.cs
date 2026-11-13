using System.Runtime.CompilerServices;

// Debug-only, like Forge.Scripting, Forge.Approval and Forge.HostClient: the agent slot pool is internal (it has no
// public host surface yet), so the concurrency contract of its dynamic limit is pinned by direct unit tests rather
// than through AgentHost. A Release build exposes nothing.
#if DEBUG
[assembly: InternalsVisibleTo("Forge.Core.Tests")]
#endif
