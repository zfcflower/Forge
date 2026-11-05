using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Serializes the SingleUseExecutor tests that read or write files under <see cref="System.AppContext.BaseDirectory"/>
/// (the shadow-host copy and the same-named decoy assembly) so the copy never races a concurrent write.</summary>
[CollectionDefinition("SingleUseExecutorFilesystem", DisableParallelization = true)]
public sealed class SingleUseExecutorFilesystemCollection
{
}
