using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Shared per-provider probing schedule (5, 10, 20, 60 minutes, then 60, each with +-10% jitter). Waiters on the
/// same provider share one probe loop.
/// </summary>
public sealed class QuotaProbeSchedule
{
    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(60),
    ];

    private readonly Func<double> _random;
    private readonly TimeProvider _time;
    private readonly Action<string, Exception>? _onProbeError;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _steps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Loop> _loops = new(StringComparer.Ordinal);

    public QuotaProbeSchedule(
        Func<double>? random = null,
        TimeProvider? time = null,
        Action<string, Exception>? onProbeError = null)
    {
        _random = random ?? Random.Shared.NextDouble;
        _time = time ?? TimeProvider.System;
        _onProbeError = onProbeError;
    }

    public static string KeyFor(string? providerId) => string.IsNullOrEmpty(providerId) ? "default" : providerId;

    /// <summary>The next wait for this key (jittered) and advances its step.</summary>
    public TimeSpan NextDelay(string providerKey)
    {
        int step;
        lock (_gate)
        {
            _steps.TryGetValue(providerKey, out step);
            _steps[providerKey] = step + 1;
        }

        var baseDelay = Steps[Math.Min(step, Steps.Length - 1)];
        var factor = 0.9 + (0.2 * Math.Clamp(_random(), 0d, 1d));
        return baseDelay * factor;
    }

    /// <summary>Forgets the backoff of this key (the quota is available again).</summary>
    public void Reset(string providerKey)
    {
        lock (_gate)
        {
            _steps.Remove(providerKey);
        }
    }

    /// <summary>
    /// Completes with true once a probe succeeds. One probe loop runs per key; it stops when it succeeds or the last
    /// waiter cancels. A cancelled waiter throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<bool> WaitUntilAvailableAsync(
        string providerKey, ModelSettings model, IProviderQuotaProbe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(probe);
        ct.ThrowIfCancellationRequested();

        Loop loop;
        lock (_gate)
        {
            if (_loops.TryGetValue(providerKey, out var existing))
            {
                loop = existing;
                loop.Waiters++;
            }
            else
            {
                loop = new Loop { Waiters = 1 };
                _loops[providerKey] = loop;
                var started = loop;
                loop.Task = Task.Run(() => RunAsync(providerKey, model, probe, started), CancellationToken.None);
            }
        }

        try
        {
            return await loop.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Release(providerKey, loop);
            throw;
        }
    }

    private void Release(string providerKey, Loop loop)
    {
        lock (_gate)
        {
            if (--loop.Waiters <= 0)
            {
                if (_loops.TryGetValue(providerKey, out var current) && ReferenceEquals(current, loop))
                {
                    _loops.Remove(providerKey);
                }

                loop.Cts.Cancel();
            }
        }
    }

    private async Task<bool> RunAsync(string providerKey, ModelSettings model, IProviderQuotaProbe probe, Loop loop)
    {
        var ct = loop.Cts.Token;
        try
        {
            while (true)
            {
                await Task.Delay(NextDelay(providerKey), _time, ct).ConfigureAwait(false);
                try
                {
                    if (await probe.ProbeAsync(model, ct).ConfigureAwait(false))
                    {
                        Reset(providerKey);
                        return true;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    try
                    {
                        _onProbeError?.Invoke(providerKey, ex);
                    }
                    catch (Exception)
                    {
                        // A faulty callback must not stop the loop.
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_loops.TryGetValue(providerKey, out var current) && ReferenceEquals(current, loop))
                {
                    _loops.Remove(providerKey);
                }
            }
        }
    }

    private sealed class Loop
    {
        public CancellationTokenSource Cts { get; } = new();

        public int Waiters;

        public Task<bool> Task = System.Threading.Tasks.Task.FromResult(false);
    }
}
