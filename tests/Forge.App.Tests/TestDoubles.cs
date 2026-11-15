using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Providers;

namespace Forge.App.Tests;

/// <summary>An HTTP handler for suites that must never reach the network: any request throws instead of dialing out.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("no network in this test");
}

/// <summary>The "no provider configured" shape a session-backed view model needs to start.</summary>
internal static class TestProviderServices
{
    /// <summary>Provider services over a blank provider record written into <paramref name="root"/>, with no network handler.</summary>
    public static ProviderServices Unconfigured(string root)
    {
        var settingsPath = Path.Combine(root, "providers-settings.json");
        File.WriteAllText(settingsPath, """{"provider":{"kind":"","baseUrl":""}}""");
        var dataDir = Path.Combine(root, "provider-data");
        Directory.CreateDirectory(dataDir);
        // Fake handler, never used for network here; the workspace does not own it so it is left for reclamation.
        return new ProviderServices(settingsPath, dataDir, new HttpClient(new ThrowingHandler()));
    }

    /// <summary>The same services over a throw-away probe directory that is deleted before this returns.</summary>
    public static ProviderServices UnconfiguredProbe()
    {
        var probe = TestTempFiles.NewDirectory("forge-unconfigured-");
        try
        {
            var settingsPath = Path.Combine(probe, "settings.json");
            File.WriteAllText(settingsPath, """{"provider":{"kind":"","baseUrl":""}}""");
            var dataDir = Path.Combine(probe, "data");
            Directory.CreateDirectory(dataDir);
            return new ProviderServices(settingsPath, dataDir, new HttpClient(new ThrowingHandler()));
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
        }
    }
}

/// <summary>One notification a call site asked for: the level, the two text keys and the deduplication key.</summary>
internal sealed record NotificationCall(NotificationLevel Level, string? TitleKey, string? BodyKey, string? Key);

/// <summary>
/// Records every notification a call site asks for, so a wiring test can pin "which path notifies, at which level, under
/// which key" (App SPEC 200). Set <see cref="Failure"/> to make the entry throw instead, for the fire-and-forget paths
/// that must never let a notification failure reach the work that wanted to notify.
/// </summary>
internal sealed class RecordingNotifications : INotifications
{
    private readonly List<NotificationCall> calls = [];

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<NotificationCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    /// <summary>The calls whose deduplication key is <paramref name="key"/>.</summary>
    public IReadOnlyList<NotificationCall> WithKey(string key) =>
        [.. Calls.Where(call => string.Equals(call.Key, key, StringComparison.Ordinal))];

    /// <summary>When set, <see cref="NotifyAsync"/> throws it instead of recording.</summary>
    public Exception? Failure { get; init; }

    public Task NotifyAsync(
        NotificationLevel level,
        LocText title,
        LocText body,
        string? key = null,
        CancellationToken ct = default)
    {
        if (Failure is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (calls)
        {
            calls.Add(new NotificationCall(level, title.Key, body.Key, key));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The process-wide <see cref="Forge.App.Localization.Loc.LanguageChanged"/> subscription list, read by reflection so a
/// test can raise one subscriber's own handler without switching the language for every other subscriber in the process
/// (the event is field-like, so its invocation list is the only seam). The Git panel suite has its own copy of this
/// probe; this one is the shared registration for everybody else.
/// </summary>
internal static class LanguageSubscriptions
{
    /// <summary>The objects currently subscribed, one entry per handler.</summary>
    public static List<object?> Subscribers() => Handlers().Select(static handler => handler.Target).ToList();

    /// <summary>
    /// Invokes only <paramref name="subscriber"/>'s handlers on a thread-pool thread, exactly as a language switch raised
    /// off the building thread would reach it. Returns how many handlers ran and the managed id of the thread they ran on.
    /// </summary>
    public static Task<(int Handlers, int ThreadId)> RaiseOnPoolThreadAsync(object subscriber)
    {
        var handlers = HandlersOf(subscriber);
        return Task.Run(
            () =>
            {
                Invoke(handlers);
                return (handlers.Length, Environment.CurrentManagedThreadId);
            });
    }

    /// <summary>Invokes only <paramref name="subscriber"/>'s handlers on the calling thread; returns how many ran.</summary>
    public static int RaiseHere(object subscriber)
    {
        var handlers = HandlersOf(subscriber);
        Invoke(handlers);
        return handlers.Length;
    }

    /// <summary>
    /// Unsubscribes <paramref name="subscriber"/> again, for the few view models that offer no <c>Dispose</c> of their
    /// own: a test must not leave a subscriber behind in a process-wide event.
    /// </summary>
    public static void Detach(object subscriber)
    {
        var field = EventField();
        var current = (MulticastDelegate?)field.GetValue(Forge.App.Localization.Loc.Instance);
        foreach (var handler in HandlersOf(subscriber))
        {
            current = (MulticastDelegate?)Delegate.Remove(current, handler);
        }

        field.SetValue(Forge.App.Localization.Loc.Instance, current);
    }

    private static Delegate[] HandlersOf(object subscriber) =>
        Handlers().Where(handler => ReferenceEquals(handler.Target, subscriber)).ToArray();

    private static Delegate[] Handlers() =>
        ((MulticastDelegate?)EventField().GetValue(Forge.App.Localization.Loc.Instance))?.GetInvocationList() ?? [];

    private static System.Reflection.FieldInfo EventField() =>
        typeof(Forge.App.Localization.Loc)
            .GetField("LanguageChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Loc.LanguageChanged is no longer a field-like event; update this helper.");

    private static void Invoke(Delegate[] handlers)
    {
        foreach (var handler in handlers)
        {
            handler.DynamicInvoke(Forge.App.Localization.Loc.Instance, EventArgs.Empty);
        }
    }
}
