using System.Security.Cryptography;
using System.Text;
using Forge.Approval;

namespace Forge.Approval.Tests;

internal static class ApprovalFixtures
{
    public const string PluginId = "acme.tools";
    public const string Version = "1.2.0";
    public const string Sdk = "10.0.100";
    public const string Source = "public sealed class Tool { }";

    public static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly string ApprovedDependencyHash =
        Hashing.DependencyHash([new Dependency("Newtonsoft.Json", "13.0.3")]);

    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static PluginRecord ApprovedRecord(
        string source = Source,
        string? dependencyHash = null,
        string sdkVersion = Sdk)
    {
        var sourceHash = Hashing.SourceHash(source);
        var dependency = dependencyHash ?? ApprovedDependencyHash;
        return new PluginRecord
        {
            PluginId = PluginId,
            Version = Version,
            SourceHash = sourceHash,
            DependencyHash = dependency,
            SdkVersion = sdkVersion,
            ApprovedHash = Hashing.ApprovalFingerprint(sourceHash, dependency, sdkVersion),
            Status = PluginStatus.Approved,
            CreatedAt = Epoch,
            UpdatedAt = Epoch,
        };
    }

    public static PluginRecord Candidate(
        string source = Source,
        string? dependencyHash = null,
        string sdkVersion = Sdk,
        PluginStatus status = PluginStatus.Draft) =>
        new()
        {
            PluginId = PluginId,
            Version = Version,
            SourceHash = Hashing.SourceHash(source),
            DependencyHash = dependencyHash ?? ApprovedDependencyHash,
            SdkVersion = sdkVersion,
            Status = status,
        };

    public static ApprovalStore StoreWithApproval(string directory, string source = Source)
    {
        var store = new ApprovalStore(directory);
        store.SetApproved(PluginId, Version, Hashing.SourceHash(source), ApprovedDependencyHash, Sdk);
        return store;
    }
}
