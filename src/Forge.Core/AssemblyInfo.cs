using System.Runtime.CompilerServices;

// Unconditional, unlike Forge.Approval and Forge.Scripting: the agent slot pool tests are compiled in every configuration
// (they pin the pool's locking and permit accounting, so they must run in Release too), and a Release build therefore has
// to expose the internal pool to them or the solution does not compile. The grant names a test assembly that is never
// shipped.
[assembly: InternalsVisibleTo("Forge.Core.Tests")]
