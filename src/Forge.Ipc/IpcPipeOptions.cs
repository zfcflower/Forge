namespace Forge.Ipc;

/// <summary>Tuning for the named-pipe transports and the frame reader.</summary>
public sealed record IpcPipeOptions
{
    /// <summary>Largest JSON body accepted or sent; larger frames are rejected.</summary>
    public int MaxFrameBytes { get; init; } = IpcProtocol.MaxFrameBytes;

    /// <summary>Upper bound on waiting for a peer: a server waiting for a client, a client connecting.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Pipe buffer used by the underlying stream.</summary>
    public int ReadBufferBytes { get; init; } = 64 * 1024;

    /// <summary>Pipe buffer used by the underlying stream.</summary>
    public int WriteBufferBytes { get; init; } = 64 * 1024;

    /// <summary>Number of complete frames queued for delivery before the reader waits.</summary>
    public int FrameQueueCapacity { get; init; } = 1024;

    /// <summary>Bytes of queued frames buffered ahead of delivery before the reader waits.</summary>
    public int FrameQueueBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Largest declared frame length that is skipped to resynchronize; anything above closes the pipe.</summary>
    public long MaxResynchronizeBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Upper bound on draining the read and dispatch loops during a stop.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
