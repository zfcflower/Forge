using MessagePack;

namespace Forge.Ipc.Tests.Support;

[MessagePackObject]
public sealed partial record EchoArgs(
    [property: Key(0)] string Text,
    [property: Key(1)] int N);

[MessagePackObject]
public sealed partial record EchoResult(
    [property: Key(0)] string Text,
    [property: Key(1)] int N,
    [property: Key(2)] bool Ok);

/// <summary>Same arity as <see cref="IpcHeader"/> but with a string where the header expects the int version.</summary>
[MessagePackObject]
public sealed partial record WrongTypedHeader(
    [property: Key(0)] string Version,
    [property: Key(1)] long Id,
    [property: Key(2)] string Operation,
    [property: Key(3)] bool IsResponse,
    [property: Key(4)] IpcError? Error);
