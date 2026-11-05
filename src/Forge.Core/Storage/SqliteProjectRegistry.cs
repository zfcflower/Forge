using Forge.Abstractions;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Storage;

/// <summary>
/// Project registry stored in <c>forge.db</c>. Roots are stored as full paths with the trailing separator trimmed and
/// a separate key column for uniqueness: the key folds case on Windows and keeps case elsewhere, so the same folder
/// opened as <c>D:\Repo</c> and <c>d:\repo</c> is one project on Windows and two projects on Linux. A partial unique
/// index allows at most one <see cref="ProjectKind.General"/> row.
/// </summary>
public sealed class SqliteProjectRegistry : IProjectRegistry, IAsyncDisposable, IDisposable
{
    private const string Columns = "id, name, root_path, kind, sort_order, added_at";

    private readonly SqliteWriter _writer;
    private readonly string _databasePath;
    private int _disposed;

    public SqliteProjectRegistry(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = StorageDb.DatabasePath(directory);
        _writer = new SqliteWriter(StorageDb.ConnectionString(_databasePath), _databasePath);
    }

    public SqliteProjectRegistry(SqliteConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(connectionString.DataSource, nameof(connectionString));
        _databasePath = Path.GetFullPath(connectionString.DataSource);
        _writer = new SqliteWriter(StorageDb.ConnectionString(connectionString), _databasePath);
    }

    public string ConnectionString => _writer.ConnectionString;

    /// <summary>Full path of the database file this store reads and writes.</summary>
    public string DatabasePath => _databasePath;

    public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) =>
        _writer.ReadAsync(
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {Columns} FROM projects ORDER BY sort_order, added_at, id";
                var projects = new List<ProjectInfo>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    projects.Add(ReadProject(reader));
                }

                return (IReadOnlyList<ProjectInfo>)projects;
            },
            ct);

    /// <summary>
    /// Registers <paramref name="rootPath"/>, or returns the project already registered at that path. Registering a
    /// second <see cref="ProjectKind.General"/> project re-points the existing one at <paramref name="rootPath"/>,
    /// because the general workspace is a singleton whose location is user-configurable.
    /// </summary>
    public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = NormalizeRoot(rootPath);
        var key = RootKey(normalized);
        return _writer.WriteAsync(
            async (connection, transaction) =>
            {
                var existing = await FindByRootKeyAsync(connection, transaction, key, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.Kind == kind)
                    {
                        return existing;
                    }

                    throw new InvalidOperationException(
                        $"'{existing.RootPath}' is already registered as a {existing.Kind} project.");
                }

                var now = DateTimeOffset.UtcNow;
                if (kind == ProjectKind.General)
                {
                    var general = await FindGeneralAsync(connection, transaction, ct).ConfigureAwait(false);
                    if (general is not null)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            "UPDATE projects SET name = $name, root_path = $path, root_key = $key WHERE id = $id",
                            ct,
                            ("$name", name),
                            ("$path", normalized),
                            ("$key", key),
                            ("$id", general.Id)).ConfigureAwait(false);
                        return general with { Name = name, RootPath = normalized };
                    }
                }

                var sortOrder = await NextSortOrderAsync(connection, transaction, ct).ConfigureAwait(false);
                var project = new ProjectInfo(
                    Guid.NewGuid().ToString("n"),
                    name,
                    normalized,
                    kind,
                    sortOrder,
                    now);
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO projects ({Columns}, root_key) VALUES ($id, $name, $path, $kind, $order, $at, $key)",
                    ct,
                    ("$id", project.Id),
                    ("$name", project.Name),
                    ("$path", project.RootPath),
                    ("$kind", KindName(project.Kind)),
                    ("$order", project.SortOrder),
                    ("$at", StorageDb.FormatTimestamp(project.AddedAt)),
                    ("$key", key)).ConfigureAwait(false);
                return project;
            },
            ct);
    }

    public Task<bool> RemoveAsync(string projectId, CancellationToken ct)
    {
        StoragePaths.SafeSegment(projectId, nameof(projectId));
        return _writer.WriteAsync(
            async (connection, transaction) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM projects WHERE id = $id",
                    ct,
                    ("$id", projectId)).ConfigureAwait(false) > 0,
            ct);
    }

    public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct)
    {
        StoragePaths.SafeSegment(projectId, nameof(projectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _writer.WriteAsync(
            async (connection, transaction) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE projects SET name = $name WHERE id = $id",
                    ct,
                    ("$name", name),
                    ("$id", projectId)).ConfigureAwait(false) > 0,
            ct);
    }

    public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct)
    {
        StoragePaths.SafeSegment(projectId, nameof(projectId));
        return _writer.WriteAsync(
            async (connection, transaction) =>
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE projects SET sort_order = $order WHERE id = $id",
                    ct,
                    ("$order", sortOrder),
                    ("$id", projectId)).ConfigureAwait(false) > 0,
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _writer.Dispose();
    }

    /// <summary>Full path with a redundant trailing separator removed; a root such as <c>C:\</c> keeps its separator.</summary>
    public static string NormalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    /// <summary>The comparison key of <see cref="NormalizeRoot"/>: case-folded on Windows, verbatim elsewhere.</summary>
    public static string RootKey(string normalizedRootPath) =>
        OperatingSystem.IsWindows()
            ? normalizedRootPath.ToUpperInvariant()
            : normalizedRootPath;

    public static string KindName(ProjectKind kind) => kind switch
    {
        ProjectKind.Repository => StorageSchema.KindRepository,
        ProjectKind.General => StorageSchema.KindGeneral,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown project kind."),
    };

    internal static ProjectKind ParseKind(string value, string databasePath, string row) => value switch
    {
        StorageSchema.KindRepository => ProjectKind.Repository,
        StorageSchema.KindGeneral => ProjectKind.General,
        _ => throw StorageDb.Malformed(databasePath, "kind", row, $"'{value}' is not one of Repository, General", null),
    };

    private ProjectInfo ReadProject(SqliteDataReader reader)
    {
        var id = StorageDb.ReadText(reader, 0, "id", _databasePath, "a project whose id cannot be read");
        var row = $"project {id}";
        return new ProjectInfo(
            id,
            StorageDb.ReadText(reader, 1, "name", _databasePath, row),
            StorageDb.ReadText(reader, 2, "root_path", _databasePath, row),
            ParseKind(StorageDb.ReadText(reader, 3, "kind", _databasePath, row), _databasePath, row),
            (int)StorageDb.ReadInt64(reader, 4, "sort_order", _databasePath, row),
            StorageDb.ParseCanonicalTimestamp(
                StorageDb.ReadText(reader, 5, "added_at", _databasePath, row),
                "added_at",
                _databasePath,
                row));
    }

    private async Task<ProjectInfo?> FindByRootKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM projects WHERE root_key = $key";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private async Task<ProjectInfo?> FindGeneralAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM projects WHERE kind = $kind LIMIT 1";
        command.Parameters.AddWithValue("$kind", StorageSchema.KindGeneral);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    private static async Task<int> NextSortOrderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM projects";
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (int)(value is long order ? order : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
