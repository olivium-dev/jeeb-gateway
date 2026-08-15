using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;

namespace CmsBundlerSnapshotExporter;

public static class Program
{
    private static readonly Regex NamespacePattern = new(
        "^[a-z0-9][a-z0-9._-]{0,99}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        ExportOptions options;
        try
        {
            options = ExportOptions.Parse(args);
            var snapshot = await ReadSnapshotAsync(options, CancellationToken.None);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
            await WriteNewFileAtomicallyAsync(options.OutputPath, bytes);

            var versions = snapshot.Documents.Sum(document => document.Versions.Count);
            var publications = snapshot.Documents.Sum(document => document.Publications.Count);
            var drafts = snapshot.Documents.Count(document =>
                document.Versions.Count > document.Publications.Count);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "exported",
                snapshot.Namespace,
                documents = snapshot.Documents.Count,
                versions,
                publications,
                drafts,
                sha256 = sha,
                output = Path.GetFullPath(options.OutputPath),
            }));
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"argument error: {ex.Message}");
            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"export failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<BundlerSnapshot> ReadSnapshotAsync(
        ExportOptions options,
        CancellationToken ct)
    {
        var connectionString = await ReadSecretAsync(
            options.ConnectionStringFile, ct);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, ct);
        await using (var readOnly = new NpgsqlCommand(
                         "SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(ct);
        }

        const string sql = """
            SELECT s.surface_id,
                   s.title,
                   s.draft::text,
                   s.created_at,
                   s.updated_at,
                   v.version,
                   v.config::text,
                   v.published_at,
                   v.published_by
              FROM cms_surfaces AS s
              LEFT JOIN cms_surface_versions AS v
                ON v.surface_id = s.surface_id
             ORDER BY s.surface_id COLLATE "C", v.version
            """;

        var sources = new List<SourceSurface>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.CommandTimeout = 30;
            await using var reader = await command.ExecuteReaderAsync(ct);
            SourceSurface? current = null;
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                if (current is null
                    || !string.Equals(current.Key, key, StringComparison.Ordinal))
                {
                    current = new SourceSurface(
                        key,
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : ParseObject(reader.GetString(2), key, "draft"),
                        reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime(),
                        reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime());
                    sources.Add(current);
                }

                if (!reader.IsDBNull(5))
                {
                    current.Published.Add(new SourcePublishedVersion(
                        reader.GetInt32(5),
                        ParseObject(reader.GetString(6), key, "published config"),
                        reader.GetFieldValue<DateTimeOffset>(7).ToUniversalTime(),
                        reader.GetString(8)));
                }
            }
        }

        await transaction.CommitAsync(ct);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "cms_surfaces contains no rows; refusing to emit an empty ownership snapshot.");
        }

        var documents = sources
            .Select(source => MapDocument(source, options.DraftCreatedBy))
            .ToList();
        return new BundlerSnapshot(1, options.Namespace, true, documents);
    }

    private static DocumentSnapshot MapDocument(
        SourceSurface source,
        string draftCreatedBy)
    {
        if (source.Published.Count == 0)
        {
            throw new InvalidOperationException(
                $"surface '{source.Key}' has no published version; Bundler requires version 1.");
        }
        if (string.IsNullOrWhiteSpace(source.Title))
        {
            throw new InvalidOperationException(
                $"surface '{source.Key}' has no title.");
        }

        var versions = new List<VersionSnapshot>();
        var publications = new List<PublicationSnapshot>();
        for (var index = 0; index < source.Published.Count; index++)
        {
            var published = source.Published[index];
            var expected = index + 1;
            if (published.Version != expected)
            {
                throw new InvalidOperationException(
                    $"surface '{source.Key}' published versions are not contiguous from 1 "
                    + $"(got {published.Version}, expected {expected}).");
            }
            if (string.IsNullOrWhiteSpace(published.PublishedBy))
            {
                throw new InvalidOperationException(
                    $"surface '{source.Key}' version {published.Version} has no publisher.");
            }

            versions.Add(new VersionSnapshot(
                published.Version,
                Envelope(source.Title, published.Content),
                published.PublishedBy,
                published.PublishedAt));
            publications.Add(new PublicationSnapshot(
                published.Version,
                published.Version,
                published.PublishedBy,
                published.PublishedAt));
        }

        if (source.Draft is not null)
        {
            versions.Add(new VersionSnapshot(
                versions.Count + 1,
                Envelope(source.Title, source.Draft.Value),
                draftCreatedBy,
                source.UpdatedAt));
        }

        var updatedAt = new[]
        {
            source.UpdatedAt,
            source.Published.Max(version => version.PublishedAt),
        }.Max();
        return new DocumentSnapshot(
            source.Key,
            source.CreatedAt,
            updatedAt,
            versions,
            publications);
    }

    private static JsonElement ParseObject(
        string json,
        string key,
        string field)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"surface '{key}' {field} is not a JSON object.");
        }
        return document.RootElement.Clone();
    }

    private static JsonElement Envelope(string title, JsonElement config) =>
        JsonSerializer.SerializeToElement(new
        {
            title,
            config,
        }, Json);

    private static async Task<string> ReadSecretAsync(
        string path,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 65_536)
        {
            throw new InvalidOperationException(
                "the connection-string file is missing, empty, or too large.");
        }
        var value = (await File.ReadAllTextAsync(info.FullName, ct)).Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException(
                "the connection-string file is empty.");
        }
        return value;
    }

    private static async Task WriteNewFileAtomicallyAsync(
        string outputPath,
        byte[] body)
    {
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"output already exists: {fullPath}");
        }
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException(
                            "output path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, body);
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Usage: CmsBundlerSnapshotExporter
          --connection-string-file <absolute-secret-path>
          --namespace <bundler-namespace>
          --draft-created-by <actor>
          --output <new-snapshot-path>

        The source transaction is REPEATABLE READ + READ ONLY. The output file
        must not already exist and contains completeNamespace=true.
        """);

    private sealed record SourcePublishedVersion(
        int Version,
        JsonElement Content,
        DateTimeOffset PublishedAt,
        string PublishedBy);

    private sealed class SourceSurface(
        string key,
        string title,
        JsonElement? draft,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        public string Key { get; } = key;
        public string Title { get; } = title;
        public JsonElement? Draft { get; } = draft;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset UpdatedAt { get; } = updatedAt;
        public List<SourcePublishedVersion> Published { get; } = [];
    }
}

public sealed class ExportOptions
{
    public required string ConnectionStringFile { get; init; }
    public required string Namespace { get; init; }
    public required string DraftCreatedBy { get; init; }
    public required string OutputPath { get; init; }

    public static ExportOptions Parse(string[] args)
    {
        string? connectionFile = null;
        string? namespaceName = null;
        string? draftActor = null;
        string? output = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--connection-string-file":
                    connectionFile = Value(args, ref index);
                    break;
                case "--namespace":
                    namespaceName = Value(args, ref index);
                    break;
                case "--draft-created-by":
                    draftActor = Value(args, ref index);
                    break;
                case "--output":
                    output = Value(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[index]}'");
            }
        }

        if (string.IsNullOrWhiteSpace(connectionFile)
            || !Path.IsPathRooted(connectionFile))
            throw new ArgumentException(
                "--connection-string-file must be an absolute path");
        if (string.IsNullOrWhiteSpace(namespaceName)
            || !Regex.IsMatch(namespaceName, "^[a-z0-9][a-z0-9._-]{0,99}$"))
            throw new ArgumentException("--namespace is invalid");
        if (string.IsNullOrWhiteSpace(draftActor)
            || draftActor.Trim().Length > 200)
            throw new ArgumentException(
                "--draft-created-by must contain 1 to 200 characters");
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--output is required");

        return new ExportOptions
        {
            ConnectionStringFile = Path.GetFullPath(connectionFile),
            Namespace = namespaceName.Trim(),
            DraftCreatedBy = draftActor.Trim(),
            OutputPath = output,
        };
    }

    private static string Value(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException("an option value is missing");
        return args[index];
    }
}

public sealed record BundlerSnapshot(
    int FormatVersion,
    string Namespace,
    bool CompleteNamespace,
    IReadOnlyList<DocumentSnapshot> Documents);

public sealed record DocumentSnapshot(
    string Key,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<VersionSnapshot> Versions,
    IReadOnlyList<PublicationSnapshot> Publications);

public sealed record VersionSnapshot(
    int Version,
    JsonElement Content,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record PublicationSnapshot(
    int Publication,
    int Version,
    string PublishedBy,
    DateTimeOffset PublishedAt);
