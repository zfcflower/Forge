using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Providers;
using Forge.Core.Net;
using Forge.IO;

namespace Forge.App.Network;

/// <summary>
/// The <c>network</c> section of <c>settings.json</c>:
/// <c>{"dns":"system|servers|doh","dnsServers":["1.1.1.1"],"dohUrl":"https://…","proxy":"system|none|manual","proxyUrl":"socks5://…","proxyBypass":["*.corp"]}</c>;
/// an omitted field is its default (<see cref="NetworkSettings.Default"/>). Follows the file rules of
/// <see cref="Agents.CompactionSettingsStore"/>: the file is read as a node tree and only the <c>network</c> node is
/// replaced, so other sections and unknown fields survive; writes take <see cref="SettingsFileMutex"/> and are atomic, and
/// a file that is not a JSON object is refused rather than overwritten. Loading never throws: an invalid value is replaced
/// by its default (a list keeps its valid entries; a mode whose server, URL or proxy is missing falls back to
/// <c>system</c>) and the replaced fields are named in <see cref="LoadProblem"/>.
/// </summary>
internal sealed class NetworkSettingsStore
{
    public const string SectionName = "network";

    private readonly string settingsPath;
    private NetworkSettings current = NetworkSettings.Default;
    private string? loadProblem;

    public NetworkSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = settingsPath;
        Reload();
    }

    public NetworkSettings Current => Volatile.Read(ref current);

    /// <summary>The fields the last load replaced by defaults (<c>field: value</c>, semicolon separated), or null.</summary>
    public string? LoadProblem => Volatile.Read(ref loadProblem);

    public void Reload()
    {
        var loaded = NetworkSettings.Default;
        var problems = new List<string>();
        if (File.Exists(settingsPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root && root[SectionName] is { } node)
                {
                    if (node is JsonObject section)
                    {
                        loaded = Parse(section, problems);
                    }
                    else
                    {
                        problems.Add(SectionName + ": " + node.ToJsonString());
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A broken file reads as the defaults; the preferences store reports the file itself.
            }
        }

        Volatile.Write(ref current, loaded);
        Volatile.Write(ref loadProblem, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    /// <summary>
    /// Writes the section. Throws <see cref="ArgumentException"/> when <paramref name="value"/> breaks
    /// <see cref="NetworkSettingsRules"/> and <see cref="InvalidOperationException"/> when the file is not a JSON object.
    /// </summary>
    public void Save(NetworkSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = Normalize(value);
        var problems = NetworkSettingsRules.Problems(normalized);
        if (problems.Count > 0)
        {
            throw new ArgumentException("Invalid network settings: " + string.Join(", ", problems), nameof(value));
        }

        using (SettingsFileMutex.Acquire(settingsPath))
        {
            var root = ProviderStore.ReadRootForEdit(settingsPath);
            var section = root[SectionName] as JsonObject ?? new JsonObject();
            section["dns"] = DnsToken(normalized.Dns);
            section["dnsServers"] = ToArray(normalized.DnsServers);
            SetOrRemove(section, "dohUrl", normalized.DohUrl);
            section["proxy"] = ProxyToken(normalized.Proxy);
            SetOrRemove(section, "proxyUrl", normalized.ProxyUrl);
            section["proxyBypass"] = ToArray(normalized.ProxyBypass);
            root[SectionName] = section;
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllBytes(settingsPath, Encoding.UTF8.GetBytes(json));
        }

        Volatile.Write(ref current, normalized);
        Volatile.Write(ref loadProblem, null);
    }

    public static string DnsToken(DnsMode mode) => mode switch
    {
        DnsMode.Servers => "servers",
        DnsMode.DnsOverHttps => "doh",
        _ => "system",
    };

    public static string ProxyToken(ProxyMode mode) => mode switch
    {
        ProxyMode.None => "none",
        ProxyMode.Manual => "manual",
        _ => "system",
    };

    /// <summary>Trims every text and drops empty URLs and list entries.</summary>
    public static NetworkSettings Normalize(NetworkSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value with
        {
            DnsServers = Clean(value.DnsServers),
            DohUrl = string.IsNullOrWhiteSpace(value.DohUrl) ? null : value.DohUrl.Trim(),
            ProxyUrl = string.IsNullOrWhiteSpace(value.ProxyUrl) ? null : value.ProxyUrl.Trim(),
            ProxyBypass = Clean(value.ProxyBypass),
        };
    }

    internal static NetworkSettings Parse(JsonObject section, List<string> problems)
    {
        var dns = section["dns"] switch
        {
            null => DnsMode.System,
            JsonValue v when v.TryGetValue<string>(out var s) && s.Trim().ToLowerInvariant() is var t && t is "system" or "servers" or "doh"
                => t switch { "servers" => DnsMode.Servers, "doh" => DnsMode.DnsOverHttps, _ => DnsMode.System },
            var other => Invalid(problems, "dns", other, DnsMode.System),
        };
        var servers = ReadList(section, "dnsServers", static s => NetworkSettingsRules.TryParseServer(s, out _), problems);
        var doh = ReadUrl(section, "dohUrl", NetworkSettingsRules.IsValidDohUrl, problems);
        var proxy = section["proxy"] switch
        {
            null => ProxyMode.System,
            JsonValue v when v.TryGetValue<string>(out var s) && s.Trim().ToLowerInvariant() is var t && t is "system" or "none" or "manual"
                => t switch { "none" => ProxyMode.None, "manual" => ProxyMode.Manual, _ => ProxyMode.System },
            var other => Invalid(problems, "proxy", other, ProxyMode.System),
        };
        var proxyUrl = ReadUrl(section, "proxyUrl", static s => NetworkSettingsRules.TryParseProxyUrl(s, out _, out _), problems);
        var bypass = ReadList(section, "proxyBypass", NetworkSettingsRules.IsValidBypassPattern, problems);

        if (dns == DnsMode.Servers && servers.Count == 0)
        {
            problems.Add("dns: \"servers\" without a valid dnsServers entry");
            dns = DnsMode.System;
        }

        if (dns == DnsMode.DnsOverHttps && doh is null)
        {
            problems.Add("dns: \"doh\" without a valid dohUrl");
            dns = DnsMode.System;
        }

        if (proxy == ProxyMode.Manual && proxyUrl is null)
        {
            problems.Add("proxy: \"manual\" without a valid proxyUrl");
            proxy = ProxyMode.System;
        }

        return new NetworkSettings(dns, servers, doh, proxy, proxyUrl, bypass);
    }

    private static T Invalid<T>(List<string> problems, string field, JsonNode? node, T fallback)
    {
        problems.Add(field + ": " + (node?.ToJsonString() ?? "null"));
        return fallback;
    }

    private static string? ReadUrl(JsonObject section, string field, Func<string, bool> valid, List<string> problems)
    {
        switch (section[field])
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (valid(text.Trim()))
                {
                    return text.Trim();
                }

                problems.Add(field + ": " + value.ToJsonString());
                return null;
            case var other:
                problems.Add(field + ": " + other.ToJsonString());
                return null;
        }
    }

    private static IReadOnlyList<string> ReadList(JsonObject section, string field, Func<string, bool> valid, List<string> problems)
    {
        var node = section[field];
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            problems.Add(field + ": " + node.ToJsonString());
            return [];
        }

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim() is { Length: > 0 } trimmed && valid(trimmed))
            {
                if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(trimmed);
                }
            }
            else
            {
                problems.Add(field + ": " + (item?.ToJsonString() ?? "null"));
            }
        }

        return result;
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string> values)
    {
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value?.Trim() is { Length: > 0 } trimmed && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode?)value);
        }

        return array;
    }

    private static void SetOrRemove(JsonObject section, string field, string? value)
    {
        if (value is null)
        {
            section.Remove(field);
        }
        else
        {
            section[field] = value;
        }
    }
}
