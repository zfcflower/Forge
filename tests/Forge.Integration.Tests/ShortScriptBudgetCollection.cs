using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Isolates warm-budget timing tests from parallel Roslyn load in this assembly.</summary>
[CollectionDefinition("ShortScriptBudget", DisableParallelization = true)]
public sealed class ShortScriptBudgetCollection
{
}
