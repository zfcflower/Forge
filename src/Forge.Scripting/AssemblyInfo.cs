using System.Runtime.CompilerServices;

// Debug-only, like Forge.Approval and Forge.HostClient: the tests that drive the internal compile delegate are
// compiled only in Debug (see ScriptCodeCompilerConcurrencyTests), so a Release build never exposes internals.
#if DEBUG
[assembly: InternalsVisibleTo("Forge.Scripting.Tests")]
#endif
