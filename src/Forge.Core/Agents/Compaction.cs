namespace Forge.Core.Agents;

/// <summary>What one compaction changed, in the measurement the context gauge uses: before and after, in tokens.</summary>
public sealed record CompactionResult(int TokensBefore, int TokensAfter);
