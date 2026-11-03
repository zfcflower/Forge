namespace Forge.Approval;

/// <summary>Lifecycle state of one plugin version as recorded by the approval engine.</summary>
public enum PluginStatus
{
    /// <summary>Discovered but never approved; it must not run.</summary>
    Draft,

    /// <summary>The recorded hashes were approved by the user for this version.</summary>
    Approved,

    /// <summary>The user disabled the plugin after approval; it must not run until enabled.</summary>
    Disabled,

    /// <summary>The plugin was removed from the installation; it must not run.</summary>
    Uninstalled,
}

/// <summary>
/// Persisted state for one plugin version. <see cref="SourceHash"/>, <see cref="DependencyHash"/> and
/// <see cref="SdkVersion"/> describe the content the record currently points at; <see cref="ApprovedHash"/> is the
/// <see cref="Hashing.ApprovalFingerprint"/> of the content the user approved, so any later change to source,
/// dependencies or SDK version stops matching it and invalidates the approval.
/// </summary>
public sealed record PluginRecord
{
    public string PluginId { get; init; } = "";

    public string Version { get; init; } = "";

    public string SourceHash { get; init; } = "";

    public string? ApprovedHash { get; init; }

    public string DependencyHash { get; init; } = "";

    public string SdkVersion { get; init; } = "";

    public PluginStatus Status { get; init; } = PluginStatus.Draft;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>An approved status that also carries an approved hash; anything else must not run.</summary>
    public bool HasApproval => Status == PluginStatus.Approved && !string.IsNullOrEmpty(ApprovedHash);
}
