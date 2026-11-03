using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Ipc.Tests.Support;
using Xunit;

namespace Forge.Ipc.Tests;

public sealed record RegisteredResult(string Name, int Count);

[JsonSerializable(typeof(RegisteredResult))]
public partial class RegisteredResultJsonContext : JsonSerializerContext
{
}

public sealed class IpcJsonTypesTests
{
    private sealed record UnregisteredResult(int Value);

    [Fact]
    public void Empty_object_is_an_empty_json_object()
    {
        Assert.Equal("{}", IpcJsonTypes.EmptyObject.GetRawText());
    }

    [Fact]
    public void From_id_writes_the_id_argument()
    {
        Assert.Equal("""{"id":42}""", IpcJsonTypes.FromId(42).GetRawText());
    }

    [Fact]
    public void Builtin_string_and_int_results_deserialize()
    {
        Assert.Equal("hi", IpcJsonTypes.DeserializeResult<string>(Json.Parse("\"hi\""), "op"));
        Assert.Equal(42, IpcJsonTypes.DeserializeResult<int>(Json.Parse("42"), "op"));
    }

    [Fact]
    public void Json_element_result_is_a_clone_that_outlives_its_document()
    {
        JsonElement result;
        using (var document = JsonDocument.Parse("""{"k" : [1]}"""))
        {
            result = IpcJsonTypes.DeserializeResult<JsonElement>(document.RootElement, "op");
        }

        Assert.Equal("""{"k" : [1]}""", result.GetRawText());
    }

    [Fact]
    public void Unregistered_result_type_is_rejected_by_name()
    {
        var ex = Assert.Throws<IpcProtocolException>(() => IpcJsonTypes.DeserializeResult<UnregisteredResult>(Json.Parse("{}"), "op"));

        Assert.Contains("is not registered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_with_the_wrong_shape_is_a_protocol_error_naming_the_operation()
    {
        var ex = Assert.Throws<IpcProtocolException>(() => IpcJsonTypes.DeserializeResult<int>(Json.Parse("\"abc\""), "op"));

        Assert.IsType<JsonException>(ex.InnerException);
        Assert.Contains("'op'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_result_for_a_reference_type_is_a_protocol_error()
    {
        var ex = Assert.Throws<IpcProtocolException>(() => IpcJsonTypes.DeserializeResult<string>(Json.Parse("null"), "op"));

        Assert.Contains("was null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registered_context_makes_its_types_available_as_typed_results()
    {
        IpcJsonTypes.Register(RegisteredResultJsonContext.Default);

        var value = IpcJsonTypes.DeserializeResult<RegisteredResult>(Json.Parse("""{"Name":"a","Count":2}"""), "op");

        Assert.Equal(new RegisteredResult("a", 2), value);
    }
}
