namespace Forge.Approval;

/// <summary>
/// The approval rules. Evaluation is pure and deterministic: it reads the store's in-memory records (no file
/// access) and compares them with the content hashes of the candidate version, so the same inputs always yield the
/// same decision.
/// </summary>
/// <remarks>
/// Status is owned by the persisted record: a version with no record, or one still in
/// <see cref="PluginStatus.Draft"/>, needs approval; <see cref="PluginStatus.Disabled"/> and
/// <see cref="PluginStatus.Uninstalled"/> can never run. A candidate flagged disabled or uninstalled blocks as well,
/// because a live copy in that state must not run even if the record is stale. Content always comes from the
/// candidate: an approval only holds while source hash, dependency hash and SDK version all match what was approved.
/// </remarks>
public static class ApprovalEngine
{
    /// <summary>Evaluates the candidate against the record the store holds for the same plugin id and version.</summary>
    public static ApprovalDecision Evaluate(PluginRecord candidate, ApprovalStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.PluginId) || string.IsNullOrWhiteSpace(candidate.Version))
        {
            return ApprovalDecision.Block("plugin id and version are required");
        }

        return Evaluate(candidate, store.Get(candidate.PluginId, candidate.Version));
    }

    /// <summary>Evaluates the candidate against a record already read from a store (or <c>null</c> when none exists).</summary>
    public static ApprovalDecision Evaluate(PluginRecord candidate, PluginRecord? record)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.IsNullOrWhiteSpace(candidate.PluginId) || string.IsNullOrWhiteSpace(candidate.Version))
        {
            return ApprovalDecision.Block("plugin id and version are required");
        }

        var label = $"{candidate.PluginId} {candidate.Version}";
        if (candidate.Status == PluginStatus.Disabled)
        {
            return ApprovalDecision.Block($"plugin {label} is disabled");
        }

        if (candidate.Status == PluginStatus.Uninstalled)
        {
            return ApprovalDecision.Block($"plugin {label} is uninstalled");
        }

        if (record is null)
        {
            return ApprovalDecision.Require($"no approval record for {label}");
        }

        if (!string.Equals(record.PluginId, candidate.PluginId, StringComparison.Ordinal) ||
            !string.Equals(record.Version, candidate.Version, StringComparison.Ordinal))
        {
            return ApprovalDecision.Block($"approval record does not belong to {label}");
        }

        switch (record.Status)
        {
            case PluginStatus.Approved:
                break;
            case PluginStatus.Draft:
                return ApprovalDecision.Require($"plugin {label} is not approved");
            case PluginStatus.Disabled:
                return ApprovalDecision.Block($"plugin {label} is disabled");
            case PluginStatus.Uninstalled:
                return ApprovalDecision.Block($"plugin {label} is uninstalled");
            default:
                return ApprovalDecision.Block($"plugin {label} is in an unknown state");
        }

        if (string.IsNullOrEmpty(record.ApprovedHash))
        {
            return ApprovalDecision.Require($"plugin {label} has no approved hash");
        }

        var approved = Hashing.ApprovalFingerprint(record.SourceHash, record.DependencyHash, record.SdkVersion);
        if (!string.Equals(record.ApprovedHash, approved, StringComparison.Ordinal))
        {
            return ApprovalDecision.Require($"approval for {label} was invalidated by a change to the recorded plugin");
        }

        if (string.IsNullOrEmpty(candidate.SourceHash))
        {
            return ApprovalDecision.Require($"source hash for {label} is missing");
        }

        if (!string.Equals(candidate.SourceHash, record.SourceHash, StringComparison.Ordinal))
        {
            return ApprovalDecision.Require($"source changed since approval of {label}");
        }

        if (!string.Equals(candidate.DependencyHash, record.DependencyHash, StringComparison.Ordinal))
        {
            return ApprovalDecision.Require($"dependencies changed since approval of {label}");
        }

        if (!string.Equals(candidate.SdkVersion, record.SdkVersion, StringComparison.Ordinal))
        {
            return ApprovalDecision.Require($"plugin SDK version changed since approval of {label}");
        }

        var current = Hashing.ApprovalFingerprint(candidate.SourceHash, candidate.DependencyHash, candidate.SdkVersion);
        if (!string.Equals(current, record.ApprovedHash, StringComparison.Ordinal))
        {
            return ApprovalDecision.Require($"plugin {label} no longer matches its approved hashes");
        }

        return ApprovalDecision.Allow($"approved hashes match for {label}");
    }
}
