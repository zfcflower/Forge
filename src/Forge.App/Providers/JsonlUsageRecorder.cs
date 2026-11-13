using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Providers;

/// <summary>
/// Writes one JSON object per model request to <c>&lt;data root&gt;/usage/usage-YYYYMMDD.jsonl</c>, a file per UTC day so a month of use
/// stays greppable and a stuck writer cannot grow one unbounded file.
/// </summary>
/// <remarks>
/// <para>
/// The record is written field by field rather than through reflection, so it survives trimming and native AOT, and it holds no prompt
/// text: the fields are the ones an analysis needs (shape claimed, cache lifetime, tokens by category, meters, latency, status), which
/// is what makes the file worth keeping after this build is gone.
/// </para>
/// <para>
/// Recording is best effort by contract: a full disk, a locked file or a denied directory is swallowed and reported once through the
/// diagnostics log. A diagnostic log must never be the reason a turn fails.
/// </para>
/// </remarks>
internal sealed class JsonlUsageRecorder : IUsageRecorder
{
    private readonly string directory;
    private readonly UsageLogSettingsStore? settings;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private string? reportedFailure;

    public JsonlUsageRecorder(string directory, UsageLogSettingsStore? settings = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
        this.settings = settings;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The directory the files land in, shown by the settings row so the user knows where their data goes.</summary>
    public string Directory => directory;

    /// <summary>The file a record written at <paramref name="at"/> goes to; the settings row shows it so the user knows where data lands.</summary>
    public string PathFor(DateTimeOffset at) => Path.Combine(directory, $"usage-{at.UtcDateTime:yyyyMMdd}.jsonl");

    public void Record(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (settings is { Current.Enabled: false })
        {
            return;
        }

        var at = record.At == default ? time.GetUtcNow() : record.At;
        try
        {
            lock (gate)
            {
                System.IO.Directory.CreateDirectory(directory);
                using var stream = new FileStream(PathFor(at), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new Utf8JsonWriter(stream);
                Write(writer, record with { At = at });
                writer.Flush();
                stream.WriteByte((byte)'\n');
            }

            // One line per request, so a timeline survives even when the JSONL file is switched off or unwritable. It holds
            // identifiers and counts only - never prompt text.
            AppLogs.Usage.ZLogInformation(
                $"usage {record.ProviderId}/{record.Model} ({record.Kind}, {record.Entrypoint}) ttl={record.CacheTtl}"
                + $" in={record.Usage.InputTokens} out={record.Usage.OutputTokens}"
                + $" cacheR={record.Usage.CacheReadTokens} cacheW={record.Usage.CacheWriteTokens}"
                + $" {record.Outcome} {record.ElapsedMilliseconds}ms");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (reportedFailure is null)
            {
                reportedFailure = exception.GetType().Name + ": " + exception.Message;
                AppLogs.Usage.ZLogWarning($"recording disabled: {reportedFailure}");
            }
        }
    }

    private static void Write(Utf8JsonWriter writer, UsageRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("at", record.At);
        writer.WriteString("session", record.SessionId);
        writer.WriteString("provider", record.ProviderId);
        writer.WriteString("kind", record.Kind);
        writer.WriteString("model", record.Model);
        writer.WriteString("entrypoint", record.Entrypoint);
        writer.WriteString("cacheTtl", record.CacheTtl);
        writer.WriteBoolean("downgraded", record.Downgraded);
        writer.WriteString("outcome", record.Outcome);
        if (record.StatusCode is { } status)
        {
            writer.WriteNumber("status", status);
        }

        writer.WriteStartObject("usage");
        writer.WriteNumber("input", record.Usage.InputTokens);
        writer.WriteNumber("output", record.Usage.OutputTokens);
        writer.WriteNumber("cacheRead", record.Usage.CacheReadTokens);
        writer.WriteNumber("cacheWrite", record.Usage.CacheWriteTokens);
        writer.WriteEndObject();
        writer.WriteNumber("elapsedMs", record.ElapsedMilliseconds);
        writer.WriteString("requestId", record.RequestId);

        if (record.RateLimit is { } limit)
        {
            writer.WriteStartObject("rateLimit");
            Number(writer, "fiveHour", limit.FiveHourUtilization);
            writer.WriteString("fiveHourStatus", limit.FiveHour.ToString());
            Timestamp(writer, "fiveHourReset", limit.FiveHourResetAt);
            Number(writer, "weekly", limit.WeeklyUtilization);
            writer.WriteString("weeklyStatus", limit.Weekly.ToString());
            Timestamp(writer, "weeklyReset", limit.WeeklyResetAt);
            writer.WriteString("representative", limit.Representative?.ToString());
            writer.WriteString("overage", limit.Overage.ToString());
            if (limit.Throttle is { } throttle)
            {
                writer.WriteStartObject("throttle");
                Number(writer, "requestLimit", throttle.RequestLimit);
                Number(writer, "requestRemaining", throttle.RequestRemaining);
                Number(writer, "tokenLimit", throttle.TokenLimit);
                Number(writer, "tokenRemaining", throttle.TokenRemaining);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>A number that may be absent; omitted rather than written as null so the file stays a table.</summary>
    private static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void Number(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    private static void Timestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } at)
        {
            writer.WriteString(name, at);
        }
    }
}
