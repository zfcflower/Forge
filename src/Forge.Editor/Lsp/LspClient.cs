using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Editor.Highlighting;
using Forge.Editor.Text;

namespace Forge.Editor.Lsp;

public sealed record LspClientOptions
{
    /// <summary>Workspace root sent as <c>rootUri</c>; the process working directory is not affected.</summary>
    public string? WorkspacePath { get; init; }

    public IReadOnlyList<string> WorkspaceFolders { get; init; } = [];

    public string ClientName { get; init; } = "Forge";

    public string ClientVersion { get; init; } = "1.0";

    /// <summary>Language id used for documents that do not carry one.</summary>
    public string? LanguageId { get; init; }

    public JsonNode? InitializationOptions { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public PositionEncoding PreferredPositionEncoding { get; init; } = PositionEncoding.Utf16;

    /// <summary>Upper bound on completion items kept from one response.</summary>
    public int MaxCompletionItems { get; init; } = 1000;
}

public sealed class LspClientClosedEventArgs(Exception? reason) : EventArgs
{
    public Exception? Reason { get; } = reason;
}

/// <summary>
/// LSP protocol client for one server process: handshake, document sync, diagnostics, symbols, completion, hover,
/// definition, references and semantic tokens. All state that leaves the client is immutable.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private static readonly string[] s_semanticTokenTypes = [.. SemanticTokenMapper.KnownTokenTypes.Order(StringComparer.Ordinal)];

    private static readonly string[] s_semanticTokenModifiers =
    [
        "declaration",
        "definition",
        "readonly",
        "static",
        "deprecated",
        "abstract",
        "async",
        "modification",
        "documentation",
        "defaultLibrary",
    ];

    private readonly JsonRpcConnection _connection;
    private readonly LspClientOptions _options;
    private readonly LanguageServerProcess _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, SemanticTokenState> _semanticTokens = new(StringComparer.OrdinalIgnoreCase);
    private int _closed;

    private LspClient(LanguageServerProcess process, ProcessStreams streams, LspClientOptions options)
    {
        _process = process;
        _options = options;
        _connection = new JsonRpcConnection(streams.Output, streams.Input, HandleServerRequestAsync);
        _connection.NotificationReceived += OnNotification;
        _connection.Closed += OnConnectionClosed;
    }

    public LspServerCapabilities Capabilities { get; private set; } = null!;

    public PositionEncoding PositionEncoding { get; private set; } = PositionEncoding.Utf16;

    public DiagnosticsStore Diagnostics { get; } = new();

    public LanguageServerProcess Process => _process;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public bool IsAlive => !IsClosed && _process.IsRunning;

    public event EventHandler<LspClientClosedEventArgs>? Closed;

    /// <summary>Messages the server logged through <c>window/logMessage</c>.</summary>
    public event Action<string>? LogMessage;

    /// <summary>Requests the server wants the client to redo, e.g. <c>workspace/semanticTokens/refresh</c>.</summary>
    public event Action<string>? RefreshRequested;

    public static async Task<LspClient> ConnectAsync(LanguageServerProcess process, LspClientOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        return await ConnectAsync(process, process.Start(), options, ct).ConfigureAwait(false);
    }

    /// <summary>Connects over streams the caller already took from <see cref="LanguageServerProcess.Start"/> or a restart.</summary>
    public static async Task<LspClient> ConnectAsync(
        LanguageServerProcess process,
        ProcessStreams streams,
        LspClientOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(streams);
        var settings = options ?? new LspClientOptions();
        var client = new LspClient(process, streams, settings);
        client._connection.Start();
        try
        {
            await client.InitializeAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public string UriFor(TextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Path is { Length: > 0 } path
            ? LspUri.FromPath(path)
            : throw new ArgumentException("The document has no path; LSP documents must be files.", nameof(document));
    }

    public ValueTask DidOpenAsync(TextDocument document, CancellationToken ct = default) =>
        DidOpenAsync(document, document.Snapshot, ct);

    /// <summary>Opens the document with exactly <paramref name="snapshot"/>, which callers capture to keep the server text and the sync cutoff identical.</summary>
    public ValueTask DidOpenAsync(TextDocument document, TextSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var uri = UriFor(document);
        var languageId = document.LanguageId ?? _options.LanguageId ?? "plaintext";
        return _connection.SendNotificationAsync("textDocument/didOpen", writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("textDocument");
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteString("languageId", languageId);
            writer.WriteNumber("version", snapshot.Version);
            writer.WriteString("text", snapshot.GetText());
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct);
    }

    /// <summary>Sends the minimal change when the server supports incremental sync, otherwise the whole text.</summary>
    public ValueTask DidChangeAsync(TextDocument document, TextChangedEventArgs change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var uri = UriFor(document);
        if (Capabilities.SupportsIncrementalSync)
        {
            var minimal = change.MinimalChange;
            var start = change.Old.OffsetToPosition(minimal.Offset, PositionEncoding);
            var end = change.Old.OffsetToPosition(minimal.End, PositionEncoding);
            return _connection.SendNotificationAsync("textDocument/didChange", writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("textDocument");
                writer.WriteStartObject();
                writer.WriteString("uri", uri);
                writer.WriteNumber("version", change.New.Version);
                writer.WriteEndObject();
                writer.WritePropertyName("contentChanges");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WritePropertyName("range");
                WriteRange(writer, new LspRange(ToLsp(start), ToLsp(end)));
                writer.WriteString("text", minimal.Text);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }, ct);
        }

        return DidChangeFullAsync(document, ct);
    }

    /// <summary>Sends the complete document text; used when the server only supports full sync.</summary>
    public ValueTask DidChangeFullAsync(TextDocument document, CancellationToken ct = default) =>
        DidChangeFullAsync(document, document.Snapshot, ct);

    /// <summary>Sends the complete text of exactly <paramref name="snapshot"/> so a queued edit cannot leak into an older sync batch.</summary>
    public ValueTask DidChangeFullAsync(TextDocument document, TextSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var uri = UriFor(document);
        return _connection.SendNotificationAsync("textDocument/didChange", writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("textDocument");
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteNumber("version", snapshot.Version);
            writer.WriteEndObject();
            writer.WritePropertyName("contentChanges");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("text", snapshot.GetText());
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, ct);
    }

    public ValueTask DidSaveAsync(TextDocument document, bool includeText = false, CancellationToken ct = default)
    {
        var uri = UriFor(document);
        var snapshot = document.Snapshot;
        return _connection.SendNotificationAsync("textDocument/didSave", writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("textDocument");
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteEndObject();
            if (includeText)
            {
                writer.WriteString("text", snapshot.GetText());
            }

            writer.WriteEndObject();
        }, ct);
    }

    public ValueTask DidCloseAsync(TextDocument document, CancellationToken ct = default)
    {
        var uri = UriFor(document);
        _semanticTokens.TryRemove(uri, out _);
        Diagnostics.Remove(uri);
        return _connection.SendNotificationAsync("textDocument/didClose", writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("textDocument");
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct);
    }

    public async Task<CompletionResult> RequestCompletionAsync(TextDocument document, int offset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Capabilities is { SupportsCompletion: false })
        {
            return new CompletionResult([], IsIncomplete: false);
        }

        var uri = UriFor(document);
        var position = ToLsp(document.Snapshot.OffsetToPosition(offset, PositionEncoding));
        using var timeout = CreateTimeout(ct);
        var result = await _connection.SendRequestAsync("textDocument/completion", writer =>
        {
            writer.WriteStartObject();
            WriteTextDocumentIdentifier(writer, uri);
            writer.WritePropertyName("position");
            WritePosition(writer, position);
            writer.WritePropertyName("context");
            writer.WriteStartObject();
            writer.WriteNumber("triggerKind", 1);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, timeout.Token).ConfigureAwait(false);
        return ParseCompletion(result);
    }

    public async Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync(TextDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Capabilities is { SupportsDocumentSymbols: false })
        {
            return [];
        }

        var uri = UriFor(document);
        using var timeout = CreateTimeout(ct);
        var result = await _connection.SendRequestAsync("textDocument/documentSymbol", writer =>
        {
            writer.WriteStartObject();
            WriteTextDocumentIdentifier(writer, uri);
            writer.WriteEndObject();
        }, timeout.Token).ConfigureAwait(false);
        return ParseSymbols(result);
    }

    public async Task<HoverInfo?> RequestHoverAsync(TextDocument document, int offset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Capabilities is { SupportsHover: false })
        {
            return null;
        }

        var uri = UriFor(document);
        var position = ToLsp(document.Snapshot.OffsetToPosition(offset, PositionEncoding));
        using var timeout = CreateTimeout(ct);
        var result = await _connection.SendRequestAsync("textDocument/hover", writer =>
        {
            writer.WriteStartObject();
            WriteTextDocumentIdentifier(writer, uri);
            writer.WritePropertyName("position");
            WritePosition(writer, position);
            writer.WriteEndObject();
        }, timeout.Token).ConfigureAwait(false);
        return ParseHover(result);
    }

    public Task<IReadOnlyList<LspLocation>> RequestDefinitionAsync(TextDocument document, int offset, CancellationToken ct = default) =>
        RequestLocationsAsync("textDocument/definition", document, offset, Capabilities is { SupportsDefinition: false }, null, ct);

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(
        TextDocument document,
        int offset,
        bool includeDeclaration = true,
        CancellationToken ct = default) =>
        RequestLocationsAsync("textDocument/references", document, offset, Capabilities is { SupportsReferences: false }, includeDeclaration, ct);

    public async Task<SemanticTokensResult> RequestSemanticTokensAsync(TextDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var capability = Capabilities.SemanticTokens;
        if (capability is null || !capability.Full)
        {
            return SemanticTokensResult.Empty();
        }

        var uri = UriFor(document);
        using var timeout = CreateTimeout(ct);

        if (capability.SupportsDelta && _semanticTokens.TryGetValue(uri, out var cached) && cached.ResultId is not null)
        {
            JsonElement delta;
            try
            {
                delta = await _connection.SendRequestAsync("textDocument/semanticTokens/full/delta", writer =>
                {
                    writer.WriteStartObject();
                    WriteTextDocumentIdentifier(writer, uri);
                    writer.WriteString("previousResultId", cached.ResultId);
                    writer.WriteEndObject();
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (LspProtocolException) when (!IsClosed)
            {
                _semanticTokens.TryRemove(uri, out _);
                return await RequestSemanticTokensFullAsync(uri, capability, ct).ConfigureAwait(false);
            }

            if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("edits", out var editsElement) &&
                editsElement.ValueKind == JsonValueKind.Array)
            {
                var edits = ParseSemanticEdits(editsElement);
                var data = SemanticTokensDecoder.ApplyEdits(cached.Data, edits);
                var resultId = ReadResultId(delta) ?? cached.ResultId;
                _semanticTokens[uri] = new SemanticTokenState(resultId, data);
                return new SemanticTokensResult(resultId, SemanticTokensDecoder.Decode(data, capability.Legend), capability.Legend);
            }

            var fullData = SemanticTokensDecoder.ReadData(delta);
            var fullResultId = ReadResultId(delta);
            _semanticTokens[uri] = new SemanticTokenState(fullResultId, fullData);
            return new SemanticTokensResult(fullResultId, SemanticTokensDecoder.Decode(fullData, capability.Legend), capability.Legend);
        }

        return await RequestSemanticTokensFullAsync(uri, capability, ct).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (IsClosed)
        {
            return;
        }

        try
        {
            using var timeout = CreateTimeout(ct);
            await _connection.SendRequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LspProtocolException or OperationCanceledException or IOException)
        {
        }

        try
        {
            await _connection.SendNotificationAsync("exit", null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is LspProtocolException or IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _connection.NotificationReceived -= OnNotification;
        _connection.Closed -= OnConnectionClosed;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        var result = await _connection.SendRequestAsync("initialize", WriteInitializeParams, timeout.Token).ConfigureAwait(false);
        Capabilities = LspServerCapabilities.Parse(result, _options.PreferredPositionEncoding);
        PositionEncoding = Capabilities.PositionEncoding;
        await _connection.SendNotificationAsync("initialized", static writer =>
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }, timeout.Token).ConfigureAwait(false);
    }

    private void WriteInitializeParams(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("processId", Environment.ProcessId);
        writer.WritePropertyName("clientInfo");
        writer.WriteStartObject();
        writer.WriteString("name", _options.ClientName);
        writer.WriteString("version", _options.ClientVersion);
        writer.WriteEndObject();

        if (_options.WorkspacePath is { Length: > 0 } root)
        {
            writer.WriteString("rootUri", LspUri.FromPath(root));
            writer.WriteString("rootPath", System.IO.Path.GetFullPath(root));
        }
        else
        {
            writer.WriteNull("rootUri");
        }

        writer.WritePropertyName("workspaceFolders");
        writer.WriteStartArray();
        foreach (var folder in WorkspaceFolderUris())
        {
            writer.WriteStartObject();
            writer.WriteString("uri", folder);
            writer.WriteString("name", System.IO.Path.GetFileName(folder.TrimEnd('/')) is { Length: > 0 } name ? name : folder);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("initializationOptions");
        if (_options.InitializationOptions is { } initializationOptions)
        {
            initializationOptions.WriteTo(writer);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }

        writer.WritePropertyName("capabilities");
        WriteClientCapabilities(writer);
        writer.WriteEndObject();
    }

    private void WriteClientCapabilities(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("workspace");
        writer.WriteStartObject();
        writer.WriteBoolean("applyEdit", false);
        writer.WritePropertyName("workspaceEdit");
        writer.WriteStartObject();
        writer.WriteBoolean("documentChanges", false);
        writer.WriteEndObject();
        writer.WriteBoolean("configuration", true);
        writer.WriteBoolean("workspaceFolders", true);
        writer.WritePropertyName("didChangeConfiguration");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WriteEndObject();
        writer.WritePropertyName("didChangeWatchedFiles");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WriteEndObject();
        writer.WritePropertyName("semanticTokens");
        writer.WriteStartObject();
        writer.WriteBoolean("refreshSupport", false);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WritePropertyName("textDocument");
        writer.WriteStartObject();

        writer.WritePropertyName("synchronization");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WriteBoolean("willSave", false);
        writer.WriteBoolean("willSaveWaitUntil", false);
        writer.WriteBoolean("didSave", true);
        writer.WriteEndObject();

        writer.WritePropertyName("completion");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WritePropertyName("completionItem");
        writer.WriteStartObject();
        writer.WriteBoolean("snippetSupport", false);
        writer.WriteBoolean("commitCharactersSupport", false);
        writer.WritePropertyName("documentationFormat");
        writer.WriteStartArray();
        writer.WriteStringValue("markdown");
        writer.WriteStringValue("plaintext");
        writer.WriteEndArray();
        writer.WriteBoolean("deprecatedSupport", false);
        writer.WriteBoolean("preselectSupport", false);
        writer.WriteEndObject();
        writer.WriteBoolean("contextSupport", false);
        writer.WriteEndObject();

        writer.WritePropertyName("hover");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WritePropertyName("contentFormat");
        writer.WriteStartArray();
        writer.WriteStringValue("markdown");
        writer.WriteStringValue("plaintext");
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WritePropertyName("documentSymbol");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WriteBoolean("hierarchicalDocumentSymbolSupport", true);
        writer.WritePropertyName("symbolKind");
        writer.WriteStartObject();
        writer.WritePropertyName("valueSet");
        writer.WriteStartArray();
        for (var kind = 1; kind <= 26; kind++)
        {
            writer.WriteNumberValue(kind);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WritePropertyName("definition");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WriteBoolean("linkSupport", false);
        writer.WriteEndObject();

        writer.WritePropertyName("references");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WriteEndObject();

        writer.WritePropertyName("publishDiagnostics");
        writer.WriteStartObject();
        writer.WriteBoolean("versionSupport", true);
        writer.WriteBoolean("relatedInformation", true);
        writer.WritePropertyName("tagSupport");
        writer.WriteStartObject();
        writer.WritePropertyName("valueSet");
        writer.WriteStartArray();
        writer.WriteNumberValue(1);
        writer.WriteNumberValue(2);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WritePropertyName("semanticTokens");
        writer.WriteStartObject();
        writer.WriteBoolean("dynamicRegistration", false);
        writer.WritePropertyName("requests");
        writer.WriteStartObject();
        writer.WritePropertyName("full");
        writer.WriteStartObject();
        writer.WriteBoolean("delta", true);
        writer.WriteEndObject();
        writer.WriteBoolean("range", false);
        writer.WriteEndObject();
        writer.WritePropertyName("tokenTypes");
        writer.WriteStartArray();
        foreach (var tokenType in s_semanticTokenTypes)
        {
            writer.WriteStringValue(tokenType);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("tokenModifiers");
        writer.WriteStartArray();
        foreach (var modifier in s_semanticTokenModifiers)
        {
            writer.WriteStringValue(modifier);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("formats");
        writer.WriteStartArray();
        writer.WriteStringValue("relative");
        writer.WriteEndArray();
        writer.WriteBoolean("overlappingTokenSupport", false);
        writer.WriteBoolean("multilineTokenSupport", false);
        writer.WriteEndObject();

        writer.WriteEndObject();

        writer.WritePropertyName("window");
        writer.WriteStartObject();
        writer.WriteBoolean("workDoneProgress", false);
        writer.WritePropertyName("showDocument");
        writer.WriteStartObject();
        writer.WriteBoolean("support", false);
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WritePropertyName("general");
        writer.WriteStartObject();
        writer.WritePropertyName("positionEncodings");
        writer.WriteStartArray();
        writer.WriteStringValue("utf-16");
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private IReadOnlyList<string> WorkspaceFolderUris()
    {
        var folders = new List<string>(_options.WorkspaceFolders.Count + 1);
        foreach (var folder in _options.WorkspaceFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                folders.Add(LspUri.FromPath(folder));
            }
        }

        if (folders.Count == 0 && _options.WorkspacePath is { Length: > 0 } root)
        {
            folders.Add(LspUri.FromPath(root));
        }

        return folders;
    }

    private ValueTask<JsonRpcReply> HandleServerRequestAsync(string method, JsonElement parameters, CancellationToken ct) =>
        ValueTask.FromResult(method switch
        {
            "workspace/configuration" => ConfigurationReply(parameters),
            "workspace/workspaceFolders" => WorkspaceFoldersReply(),
            "workspace/applyEdit" => JsonRpcReply.Unsupported("workspace/applyEdit is not supported by this client"),
            "window/workDoneProgress/create" or "window/showMessageRequest" or "client/registerCapability" or "client/unregisterCapability" =>
                JsonRpcReply.Null,
            "workspace/semanticTokens/refresh" or "workspace/inlayHint/refresh" or "workspace/codeLens/refresh" or "workspace/diagnostic/refresh" =>
                RefreshReply(method),
            _ => JsonRpcReply.Unsupported($"Unsupported server request '{method}'"),
        });

    private static JsonRpcReply ConfigurationReply(JsonElement parameters)
    {
        var count = parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength()
            : 0;
        return new JsonRpcReply(writer =>
        {
            writer.WriteStartArray();
            for (var i = 0; i < count; i++)
            {
                writer.WriteNullValue();
            }

            writer.WriteEndArray();
        });
    }

    private JsonRpcReply WorkspaceFoldersReply()
    {
        var folders = WorkspaceFolderUris();
        return new JsonRpcReply(writer =>
        {
            writer.WriteStartArray();
            foreach (var folder in folders)
            {
                writer.WriteStartObject();
                writer.WriteString("uri", folder);
                writer.WriteString("name", System.IO.Path.GetFileName(folder.TrimEnd('/')));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private JsonRpcReply RefreshReply(string method)
    {
        RefreshRequested?.Invoke(method);
        return JsonRpcReply.Null;
    }

    private void OnConnectionClosed(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Closed?.Invoke(this, new LspClientClosedEventArgs(reason));
    }

    private void OnNotification(string method, JsonElement message)
    {
        if (!message.TryGetProperty("params", out var parameters))
        {
            return;
        }

        switch (method)
        {
            case "textDocument/publishDiagnostics":
                PublishDiagnostics(parameters);
                break;
            case "window/logMessage":
                if (parameters.TryGetProperty("message", out var logText) && logText.ValueKind == JsonValueKind.String)
                {
                    LogMessage?.Invoke(logText.GetString() ?? string.Empty);
                }

                break;
        }
    }

    private void PublishDiagnostics(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var uri = uriElement.GetString() ?? string.Empty;
        int? version = parameters.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.Number
            ? versionElement.GetInt32()
            : null;

        var diagnostics = parameters.TryGetProperty("diagnostics", out var diagnosticsElement) &&
            diagnosticsElement.ValueKind == JsonValueKind.Array
            ? ParseDiagnostics(diagnosticsElement)
            : [];

        Diagnostics.Update(uri, version, diagnostics);
    }

    private static IReadOnlyList<LspDiagnostic> ParseDiagnostics(JsonElement array)
    {
        var result = new List<LspDiagnostic>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("message", out var messageElement))
            {
                continue;
            }

            var range = item.TryGetProperty("range", out var rangeElement) ? ParseRange(rangeElement) : default;
            var severity = item.TryGetProperty("severity", out var severityElement) && severityElement.ValueKind == JsonValueKind.Number
                ? (DiagnosticSeverity)severityElement.GetInt32()
                : DiagnosticSeverity.Error;

            string? code = null;
            if (item.TryGetProperty("code", out var codeElement))
            {
                code = codeElement.ValueKind switch
                {
                    JsonValueKind.String => codeElement.GetString(),
                    JsonValueKind.Number => codeElement.GetRawText(),
                    _ => null,
                };
            }

            var source = item.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString()
                : null;

            IReadOnlyList<LspDiagnosticRelatedInformation>? related = null;
            if (item.TryGetProperty("relatedInformation", out var relatedElement) && relatedElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<LspDiagnosticRelatedInformation>(relatedElement.GetArrayLength());
                foreach (var relatedItem in relatedElement.EnumerateArray())
                {
                    if (relatedItem.ValueKind == JsonValueKind.Object &&
                        relatedItem.TryGetProperty("location", out var locationElement) &&
                        relatedItem.TryGetProperty("message", out var relatedMessage))
                    {
                        list.Add(new LspDiagnosticRelatedInformation(ParseLocation(locationElement), relatedMessage.GetString() ?? string.Empty));
                    }
                }

                related = list;
            }

            IReadOnlyList<string>? tags = null;
            if (item.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(tagsElement.GetArrayLength());
                foreach (var tag in tagsElement.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.Number)
                    {
                        list.Add(tag.GetInt32() == 1 ? "unnecessary" : "deprecated");
                    }
                }

                tags = list;
            }

            result.Add(new LspDiagnostic(range, severity, code, source, messageElement.GetString() ?? string.Empty, related, tags));
        }

        return result;
    }

    private CompletionResult ParseCompletion(JsonElement result)
    {
        var itemsElement = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var nested) ? nested : result;
        if (itemsElement.ValueKind != JsonValueKind.Array)
        {
            return new CompletionResult([], IsIncomplete: false);
        }

        var isIncomplete = result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("isIncomplete", out var incomplete) &&
            incomplete.ValueKind == JsonValueKind.True;

        var limit = Math.Max(1, _options.MaxCompletionItems);
        // Items with defaults cannot be applied safely without the advertised itemDefaults capability.
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("itemDefaults", out var itemDefaults) && itemDefaults.ValueKind != JsonValueKind.Null)
        {
            return new CompletionResult([], isIncomplete, itemsElement.GetArrayLength());
        }

        var items = new List<CompletionItem>(Math.Min(itemsElement.GetArrayLength(), limit));
        var malformed = 0;
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (items.Count >= limit)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String)
            {
                malformed++;
                continue;
            }

            var kind = CompletionItemKind.Text;
            if (item.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind != JsonValueKind.Null)
            {
                if (kindElement.ValueKind != JsonValueKind.Number || !kindElement.TryGetInt32(out var kindValue) || kindValue is < 1 or > 25)
                {
                    malformed++;
                    continue;
                }

                kind = (CompletionItemKind)kindValue;
            }

            var detail = item.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
                ? detailElement.GetString()
                : null;

            string? documentation = null;
            if (item.TryGetProperty("documentation", out var documentationElement) && documentationElement.ValueKind != JsonValueKind.Null)
            {
                if (documentationElement.ValueKind == JsonValueKind.String)
                {
                    documentation = documentationElement.GetString();
                }
                else if (documentationElement.ValueKind == JsonValueKind.Object &&
                    documentationElement.TryGetProperty("value", out var documentationValue) && documentationValue.ValueKind == JsonValueKind.String)
                {
                    documentation = documentationValue.GetString();
                }
                else
                {
                    malformed++;
                    continue;
                }
            }

            var insertText = item.TryGetProperty("insertText", out var insertElement) && insertElement.ValueKind == JsonValueKind.String
                ? insertElement.GetString()
                : null;

            // A malformed edit must never become a 0:0 replacement; the item is excluded and counted.
            if (!TryReadCompletionEdits(item, out var textEdit, out var additionalEdits, out var insertFormat))
            {
                malformed++;
                continue;
            }

            var sortText = item.TryGetProperty("sortText", out var sortElement) && sortElement.ValueKind == JsonValueKind.String
                ? sortElement.GetString()
                : null;

            var filterText = item.TryGetProperty("filterText", out var filterElement) && filterElement.ValueKind == JsonValueKind.String
                ? filterElement.GetString()
                : null;

            items.Add(new CompletionItem(label.GetString() ?? string.Empty, kind, detail, documentation, insertText, textEdit, sortText, insertFormat, additionalEdits, filterText));
        }

        return new CompletionResult(items, isIncomplete, malformed);
    }

    private static bool TryReadCompletionEdits(
        JsonElement item,
        out LspTextEdit? textEdit,
        out IReadOnlyList<LspTextEdit>? additionalEdits,
        out InsertTextFormat format)
    {
        textEdit = null;
        additionalEdits = null;
        format = InsertTextFormat.PlainText;

        if (item.TryGetProperty("insertTextFormat", out var formatElement) && formatElement.ValueKind != JsonValueKind.Null)
        {
            if (formatElement.ValueKind != JsonValueKind.Number || !formatElement.TryGetInt32(out var formatValue) || formatValue is not (1 or 2))
            {
                return false;
            }

            format = (InsertTextFormat)formatValue;
        }

        if (item.TryGetProperty("textEdit", out var textEditElement) && textEditElement.ValueKind != JsonValueKind.Null)
        {
            textEdit = ParseTextEditStrict(textEditElement);
            if (textEdit is null)
            {
                return false;
            }
        }

        if (item.TryGetProperty("additionalTextEdits", out var additionalElement) && additionalElement.ValueKind != JsonValueKind.Null)
        {
            if (additionalElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<LspTextEdit>(additionalElement.GetArrayLength());
            foreach (var editElement in additionalElement.EnumerateArray())
            {
                var edit = ParseTextEditStrict(editElement);
                if (edit is null)
                {
                    return false;
                }

                list.Add(edit);
            }

            additionalEdits = list.Count == 0 ? null : list;
        }

        return true;
    }

    private static LspTextEdit? ParseTextEditStrict(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("newText", out var newText) || newText.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // InsertReplaceEdit is not accepted: the client does not advertise insertReplaceSupport.
        if (!element.TryGetProperty("range", out var rangeElement))
        {
            return null;
        }

        return TryParseRangeStrict(rangeElement, out var range) ? new LspTextEdit(range, newText.GetString() ?? string.Empty) : null;
    }

    private static bool TryParseRangeStrict(JsonElement element, out LspRange range)
    {
        range = default;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("start", out var startElement) || !TryParsePositionStrict(startElement, out var start) ||
            !element.TryGetProperty("end", out var endElement) || !TryParsePositionStrict(endElement, out var end) ||
            end.Line < start.Line || (end.Line == start.Line && end.Character < start.Character))
        {
            return false;
        }

        range = new LspRange(start, end);
        return true;
    }

    private static bool TryParsePositionStrict(JsonElement element, out LspPosition position)
    {
        position = default;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number ||
            !line.TryGetInt32(out var lineValue) || lineValue < 0 ||
            !element.TryGetProperty("character", out var character) || character.ValueKind != JsonValueKind.Number ||
            !character.TryGetInt32(out var characterValue) || characterValue < 0)
        {
            return false;
        }

        position = new LspPosition(lineValue, characterValue);
        return true;
    }

    private static IReadOnlyList<DocumentSymbol> ParseSymbols(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var symbols = new List<DocumentSymbol>(result.GetArrayLength());
        foreach (var item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name))
            {
                continue;
            }

            if (item.TryGetProperty("location", out var location))
            {
                var range = location.TryGetProperty("range", out var locationRange) ? ParseRange(locationRange) : default;
                symbols.Add(new DocumentSymbol(
                    name.GetString() ?? string.Empty,
                    null,
                    ReadSymbolKind(item),
                    range,
                    range,
                    []));
                continue;
            }

            symbols.Add(ParseSymbol(item));
        }

        return symbols;
    }

    private static DocumentSymbol ParseSymbol(JsonElement item)
    {
        var kind = ReadSymbolKind(item);
        var range = item.TryGetProperty("range", out var rangeElement) ? ParseRange(rangeElement) : default;
        var selection = item.TryGetProperty("selectionRange", out var selectionElement) ? ParseRange(selectionElement) : range;
        var detail = item.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
            ? detailElement.GetString()
            : null;

        IReadOnlyList<DocumentSymbol> children = [];
        if (item.TryGetProperty("children", out var childrenElement) && childrenElement.ValueKind == JsonValueKind.Array)
        {
            var list = new List<DocumentSymbol>(childrenElement.GetArrayLength());
            foreach (var child in childrenElement.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("name", out _))
                {
                    list.Add(ParseSymbol(child));
                }
            }

            children = list;
        }

        return new DocumentSymbol(
            item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            detail,
            kind,
            range,
            selection,
            children);
    }

    private static SymbolKind ReadSymbolKind(JsonElement item) =>
        item.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.Number
            ? (SymbolKind)kindElement.GetInt32()
            : SymbolKind.Unknown;

    private static HoverInfo? ParseHover(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents))
        {
            return null;
        }

        var markup = contents.ValueKind switch
        {
            JsonValueKind.String => contents.GetString() ?? string.Empty,
            JsonValueKind.Object when contents.TryGetProperty("value", out var value) => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                "\n\n",
                contents.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? string.Empty
                    : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var text) ? text.GetString() ?? string.Empty : string.Empty)),
            _ => string.Empty,
        };

        if (markup.Length == 0)
        {
            return null;
        }

        LspRange? range = result.TryGetProperty("range", out var rangeElement) ? ParseRange(rangeElement) : null;
        return new HoverInfo(markup, range);
    }

    private async Task<IReadOnlyList<LspLocation>> RequestLocationsAsync(
        string method,
        TextDocument document,
        int offset,
        bool unsupported,
        bool? includeDeclaration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (unsupported)
        {
            return [];
        }

        var uri = UriFor(document);
        var position = ToLsp(document.Snapshot.OffsetToPosition(offset, PositionEncoding));
        using var timeout = CreateTimeout(ct);
        var result = await _connection.SendRequestAsync(method, writer =>
        {
            writer.WriteStartObject();
            WriteTextDocumentIdentifier(writer, uri);
            writer.WritePropertyName("position");
            WritePosition(writer, position);
            if (includeDeclaration is { } declaration)
            {
                writer.WritePropertyName("context");
                writer.WriteStartObject();
                writer.WriteBoolean("includeDeclaration", declaration);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }, timeout.Token).ConfigureAwait(false);

        return ParseLocations(result);
    }

    private static IReadOnlyList<LspLocation> ParseLocations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
        {
            return [];
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            var list = new List<LspLocation>(result.GetArrayLength());
            foreach (var item in result.EnumerateArray())
            {
                AddLocation(item, list);
            }

            return list;
        }

        var single = new List<LspLocation>(1);
        AddLocation(result, single);
        return single;
    }

    private static void AddLocation(JsonElement item, List<LspLocation> list)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (item.TryGetProperty("targetUri", out var targetUri))
        {
            var targetRange = item.TryGetProperty("targetSelectionRange", out var selection)
                ? ParseRange(selection)
                : item.TryGetProperty("targetRange", out var range) ? ParseRange(range) : default;
            list.Add(new LspLocation(targetUri.GetString() ?? string.Empty, targetRange));
            return;
        }

        list.Add(ParseLocation(item));
    }

    private static LspLocation ParseLocation(JsonElement item)
    {
        var uri = item.TryGetProperty("uri", out var uriElement) ? uriElement.GetString() ?? string.Empty : string.Empty;
        var range = item.TryGetProperty("range", out var rangeElement) ? ParseRange(rangeElement) : default;
        return new LspLocation(uri, range);
    }

    private async Task<SemanticTokensResult> RequestSemanticTokensFullAsync(string uri, SemanticTokensCapability capability, CancellationToken ct)
    {
        using var timeout = CreateTimeout(ct);
        var result = await _connection.SendRequestAsync("textDocument/semanticTokens/full", writer =>
        {
            writer.WriteStartObject();
            WriteTextDocumentIdentifier(writer, uri);
            writer.WriteEndObject();
        }, timeout.Token).ConfigureAwait(false);

        var data = SemanticTokensDecoder.ReadData(result);
        var resultId = ReadResultId(result);
        _semanticTokens[uri] = new SemanticTokenState(resultId, data);
        return new SemanticTokensResult(resultId, SemanticTokensDecoder.Decode(data, capability.Legend), capability.Legend);
    }

    private static IReadOnlyList<SemanticTokensEdit> ParseSemanticEdits(JsonElement edits)
    {
        var list = new List<SemanticTokensEdit>(edits.GetArrayLength());
        foreach (var edit in edits.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var start = edit.TryGetProperty("start", out var startElement) ? startElement.GetInt32() : 0;
            var deleteCount = edit.TryGetProperty("deleteCount", out var deleteElement) ? deleteElement.GetInt32() : 0;
            var data = edit.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
                ? SemanticTokensDecoder.ReadIntArray(dataElement)
                : null;
            list.Add(new SemanticTokensEdit(start, deleteCount, data));
        }

        return list;
    }

    private static string? ReadResultId(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("resultId", out var resultId) &&
        resultId.ValueKind == JsonValueKind.String
            ? resultId.GetString()
            : null;

    private CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        cts.CancelAfter(_options.RequestTimeout);
        return cts;
    }

    private LspPosition ToLsp(LinePosition position) => new(position.Line, position.Character);

    private static LspRange ParseRange(JsonElement element)
    {
        var start = element.TryGetProperty("start", out var startElement) ? ParsePosition(startElement) : default;
        var end = element.TryGetProperty("end", out var endElement) ? ParsePosition(endElement) : default;
        return new LspRange(start, end);
    }

    private static LspPosition ParsePosition(JsonElement element)
    {
        var line = element.TryGetProperty("line", out var lineElement) && lineElement.ValueKind == JsonValueKind.Number ? lineElement.GetInt32() : 0;
        var character = element.TryGetProperty("character", out var characterElement) && characterElement.ValueKind == JsonValueKind.Number
            ? characterElement.GetInt32()
            : 0;
        return new LspPosition(line, character);
    }

    private static void WritePosition(Utf8JsonWriter writer, LspPosition position)
    {
        writer.WriteStartObject();
        writer.WriteNumber("line", position.Line);
        writer.WriteNumber("character", position.Character);
        writer.WriteEndObject();
    }

    private static void WriteRange(Utf8JsonWriter writer, LspRange range)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WritePosition(writer, range.Start);
        writer.WritePropertyName("end");
        WritePosition(writer, range.End);
        writer.WriteEndObject();
    }

    private static void WriteTextDocumentIdentifier(Utf8JsonWriter writer, string uri)
    {
        writer.WritePropertyName("textDocument");
        writer.WriteStartObject();
        writer.WriteString("uri", uri);
        writer.WriteEndObject();
    }

    private sealed record SemanticTokenState(string? ResultId, int[] Data);
}
