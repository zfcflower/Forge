using System.Runtime.CompilerServices;

// Unconditional, unlike Forge.Approval and Forge.Scripting: the terminal protocol tests are compiled in every
// configuration (only single test cases there are wrapped in #if DEBUG), so a Release build must expose the
// internal protocol types to them or the solution does not compile in Release. The grant names a test assembly
// that is never shipped.
[assembly: InternalsVisibleTo("Forge.HostClient.Tests")]
