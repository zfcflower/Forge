using Forge.Ipc;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient;

/// <summary>Checks the typed answers of the Extension Host before the client hands them out. The serializer already
/// refuses nil in a non-nullable member; these rules catch what still decodes (blank values, null list entries,
/// undefined enum values) and report the field that broke the answer. The null checks stay as a guard.</summary>
internal static class HostAnswerRules
{
    /// <summary>Decodes the payload of a response; a missing or mismatched payload becomes <paramref name="problem"/>.</summary>
    public static bool TryRead<T>(IpcMessage response, out T value, out string problem)
        where T : class
    {
        try
        {
            value = response.Read<T>();
            problem = string.Empty;
            return true;
        }
        catch (IpcProtocolException ex)
        {
            value = null!;
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>Validates a <c>plugins.status</c> answer: one unusable row makes the whole answer unusable instead of
    /// shortening the registry.</summary>
    public static bool TryReadPluginStatuses(
        Sdk.PluginStatusResponse response, out IReadOnlyList<Sdk.PluginStatusInfo> plugins, out string problem)
    {
        plugins = [];
        if (response.Plugins is not { } rows)
        {
            problem = "The Extension Host returned no plugin status response.";
            return false;
        }

        var reported = new Sdk.PluginStatusInfo[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            if (!TryReadPluginStatus(rows[index], $"plugins[{index}]", out reported[index], out problem))
            {
                return false;
            }
        }

        plugins = reported;
        problem = string.Empty;
        return true;
    }

    /// <summary>Validates one status row. <paramref name="label"/> names the row for the caller (<c>plugins[3]</c> in a
    /// registry answer, <c>status</c> inside a commit result).</summary>
    public static bool TryReadPluginStatus(
        Sdk.PluginStatusInfo? row, string label, out Sdk.PluginStatusInfo status, out string problem)
    {
        status = null!;
        problem = string.Empty;
        if (row is null)
        {
            problem = $"{label} is null instead of a plugin status object.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(row.PluginId))
        {
            problem = $"{label} has no plugin id.";
            return false;
        }

        var identity = $"{label} ('{row.PluginId}')";
        if (string.IsNullOrWhiteSpace(row.Status))
        {
            problem = $"{identity} has no status.";
            return false;
        }

        if (!string.Equals(row.Status, "invalid", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(row.Version))
            {
                problem = $"{identity} has no version.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.SourceHash))
            {
                problem = $"{identity} has no source hash.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.DependencyHash))
            {
                problem = $"{identity} has no dependency hash.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.SdkVersion))
            {
                problem = $"{identity} has no SDK version.";
                return false;
            }
        }

        if (!TryReadPermissions(row.Permissions, identity, out problem))
        {
            return false;
        }

        status = row with
        {
            Version = row.Version ?? string.Empty,
            SourceHash = row.SourceHash ?? string.Empty,
            DependencyHash = row.DependencyHash ?? string.Empty,
            SdkVersion = row.SdkVersion ?? string.Empty,
        };
        return true;
    }

    /// <summary>Checks a permission list (<c>plugins.status</c> rows and staged extensions alike): every entry must be
    /// present, name a defined kind, and carry no blank scope.</summary>
    public static bool TryReadPermissions(
        IReadOnlyList<Sdk.Permission>? permissions, string label, out string problem)
    {
        if (permissions is null)
        {
            problem = $"{label} has no permission list.";
            return false;
        }

        for (var position = 0; position < permissions.Count; position++)
        {
            var permission = permissions[position];
            if (permission is null || !Enum.IsDefined(permission.Kind))
            {
                problem = $"{label} has an unusable permission at permissions[{position}].";
                return false;
            }

            if (permission.Scope is { } scope && string.IsNullOrWhiteSpace(scope))
            {
                problem = $"{label} has a blank scope at permissions[{position}] ({permission.Kind}).";
                return false;
            }
        }

        problem = string.Empty;
        return true;
    }
}
