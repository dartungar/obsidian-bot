using System.Data;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class VaultSearchService
{
    private const int ChunkSize = 1_200;
    private const int ChunkOverlap = 200;
    private const int EmbeddingBatchSize = 32;
    private static readonly SemaphoreSlim IndexLock = new(1, 1);

    private readonly ILogger<VaultSearchService> _logger;
    private readonly ObsidianBotOptions _options;
    private readonly OpenAiEmbeddingClient _embeddingClient;
    private readonly string _connectionString;

    public VaultSearchService(
        ILogger<VaultSearchService> logger,
        ObsidianBotOptions options,
        OpenAiEmbeddingClient embeddingClient)
    {
        _logger = logger;
        _options = options;
        _embeddingClient = embeddingClient;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.SearchDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public async Task<IReadOnlyList<SearchResult>> SearchFullTextAsync(string query, CancellationToken ct)
    {
        var matchQuery = ToFtsMatchQuery(query);
        if (string.IsNullOrWhiteSpace(matchQuery))
        {
            return Array.Empty<SearchResult>();
        }

        await IndexLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            EnsureDatabase(connection);
            await SyncMarkdownFilesAsync(connection, ct);
            return SearchFullText(connection, matchQuery);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    public async Task UpdateIndexAsync(CancellationToken ct)
    {
        await IndexLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            EnsureDatabase(connection);
            await SyncMarkdownFilesAsync(connection, ct);

            if (_embeddingClient.IsConfigured)
            {
                await EnsureEmbeddingsAsync(connection, ct);
            }
        }
        finally
        {
            IndexLock.Release();
        }
    }

    public async Task<CombinedSearchResults> SearchCombinedAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new CombinedSearchResults(
                Array.Empty<SearchResult>(),
                Array.Empty<SearchResult>(),
                _embeddingClient.IsConfigured);
        }

        var matchQuery = ToFtsMatchQuery(query);
        await IndexLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            EnsureDatabase(connection);
            await SyncMarkdownFilesAsync(connection, ct);

            var fullText = string.IsNullOrWhiteSpace(matchQuery)
                ? Array.Empty<SearchResult>()
                : SearchFullText(connection, matchQuery);
            if (!_embeddingClient.IsConfigured)
            {
                return new CombinedSearchResults(fullText, Array.Empty<SearchResult>(), false);
            }

            await EnsureEmbeddingsAsync(connection, ct);
            var queryEmbedding = (await _embeddingClient.CreateEmbeddingsAsync([query], ct))[0];
            var semantic = SearchSemantic(connection, queryEmbedding);
            return new CombinedSearchResults(fullText, semantic, true);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchSemanticAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        await IndexLock.WaitAsync(ct);
        try
        {
            using var connection = CreateConnection();
            EnsureDatabase(connection);
            await SyncMarkdownFilesAsync(connection, ct);
            await EnsureEmbeddingsAsync(connection, ct);

            var queryEmbedding = (await _embeddingClient.CreateEmbeddingsAsync([query], ct))[0];
            return SearchSemantic(connection, queryEmbedding);
        }
        finally
        {
            IndexLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.SearchDatabasePath)!);

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(GetVectorExtensionPath());
        }
        finally
        {
            connection.EnableExtensions(false);
        }

        return connection;
    }

    private static string GetVectorExtensionPath()
    {
        var runtime = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => throw new PlatformNotSupportedException("sqlite-vec supports only x64 and ARM64 Linux deployments.")
            }
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "osx-x64",
                    Architecture.Arm64 => "osx-arm64",
                    _ => throw new PlatformNotSupportedException("sqlite-vec supports only x64 and ARM64 macOS deployments.")
                }
                : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? "win-x64"
                    : throw new PlatformNotSupportedException("No sqlite-vec binary is packaged for this platform.");

        var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "vec0.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "vec0.dylib"
                : "vec0.so";
        var path = Path.Combine(AppContext.BaseDirectory, "sqlite-vec", runtime, "native", extension);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The sqlite-vec native extension was not published with the application.",
                path);
        }

        return path;
    }

    private void EnsureDatabase(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS indexed_files (
                path TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS note_chunks (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL,
                chunk_order INTEGER NOT NULL,
                content TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_note_chunks_path ON note_chunks(path);

            CREATE VIRTUAL TABLE IF NOT EXISTS note_chunks_fts
            USING fts5(content, tokenize = 'unicode61 remove_diacritics 2');

            CREATE TABLE IF NOT EXISTS embedded_chunks (
                chunk_id INTEGER PRIMARY KEY
            );

            CREATE TABLE IF NOT EXISTS search_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);

        var dimensions = GetMetadata(connection, "embedding_dimensions");
        if (dimensions is null)
        {
            CreateEmbeddingTable(connection);
            SetMetadata(connection, "embedding_dimensions", _options.EmbeddingDimensions.ToString());
            SetMetadata(connection, "embedding_model", _options.EmbeddingModel);
            return;
        }

        if (!int.TryParse(dimensions, out var existingDimensions) ||
            existingDimensions != _options.EmbeddingDimensions)
        {
            ExecuteNonQuery(connection, "DROP TABLE IF EXISTS note_embeddings; DELETE FROM embedded_chunks;");
            CreateEmbeddingTable(connection);
            SetMetadata(connection, "embedding_dimensions", _options.EmbeddingDimensions.ToString());
            SetMetadata(connection, "embedding_model", _options.EmbeddingModel);
            return;
        }

        if (!string.Equals(GetMetadata(connection, "embedding_model"), _options.EmbeddingModel, StringComparison.Ordinal))
        {
            ExecuteNonQuery(connection, "DELETE FROM note_embeddings; DELETE FROM embedded_chunks;");
            SetMetadata(connection, "embedding_model", _options.EmbeddingModel);
        }
    }

    private void CreateEmbeddingTable(SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            $"CREATE VIRTUAL TABLE note_embeddings USING vec0(embedding float[{_options.EmbeddingDimensions}] distance_metric=cosine);");
    }

    private async Task SyncMarkdownFilesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var files = new Dictionary<string, IndexedFile>(StringComparer.Ordinal);
        foreach (var fullPath in Directory.EnumerateFiles(_options.VaultPath, "*.md", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var content = await File.ReadAllTextAsync(fullPath, ct);
                var relativePath = Path.GetRelativePath(_options.VaultPath, fullPath).Replace('\\', '/');
                files[relativePath] = new IndexedFile(content, Hash(content));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read note {NotePath} while updating search index", fullPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Could not read note {NotePath} while updating search index", fullPath);
            }
        }

        var indexedFiles = GetIndexedFiles(connection);
        var changed = false;
        using var transaction = connection.BeginTransaction();

        foreach (var removedPath in indexedFiles.Keys.Except(files.Keys, StringComparer.Ordinal))
        {
            DeleteChunksForPath(connection, transaction, removedPath);
            DeleteIndexedFile(connection, transaction, removedPath);
            changed = true;
        }

        foreach (var (path, file) in files)
        {
            if (indexedFiles.TryGetValue(path, out var indexedHash) && indexedHash == file.Hash)
            {
                continue;
            }

            DeleteChunksForPath(connection, transaction, path);
            var content = $"{Path.GetFileNameWithoutExtension(path)}\n\n{file.Content}";
            var chunkOrder = 0;
            foreach (var chunk in Chunk(content))
            {
                InsertChunk(connection, transaction, path, chunkOrder++, chunk);
            }

            UpsertIndexedFile(connection, transaction, path, file.Hash);
            changed = true;
        }

        transaction.Commit();

        if (changed)
        {
            _logger.LogInformation("Updated local search index for {Count} Markdown notes", files.Count);
        }
    }

    private async Task EnsureEmbeddingsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var missing = GetChunksWithoutEmbeddings(connection);
        for (var start = 0; start < missing.Count; start += EmbeddingBatchSize)
        {
            var batch = missing.Skip(start).Take(EmbeddingBatchSize).ToArray();
            var vectors = await _embeddingClient.CreateEmbeddingsAsync(batch.Select(chunk => chunk.Content).ToArray(), ct);

            using var transaction = connection.BeginTransaction();
            for (var index = 0; index < batch.Length; index++)
            {
                InsertEmbedding(connection, transaction, batch[index].Id, vectors[index]);
            }

            transaction.Commit();
        }
    }

    private IReadOnlyList<SearchResult> SearchFullText(SqliteConnection connection, string matchQuery)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunks.path, chunks.content
            FROM note_chunks_fts
            JOIN note_chunks AS chunks ON chunks.id = note_chunks_fts.rowid
            WHERE note_chunks_fts MATCH @query
            ORDER BY bm25(note_chunks_fts)
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@query", matchQuery);
        command.Parameters.AddWithValue("@limit", CandidateLimit);

        using var reader = command.ExecuteReader();
        return ReadDistinctResults(reader, includeDistance: false);
    }

    private IReadOnlyList<SearchResult> SearchSemantic(SqliteConnection connection, float[] embedding)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH nearest AS (
                SELECT rowid, distance
                FROM note_embeddings
                WHERE embedding MATCH @embedding AND k = @limit
            )
            SELECT chunks.path, chunks.content, nearest.distance
            FROM nearest
            JOIN note_chunks AS chunks ON chunks.id = nearest.rowid
            ORDER BY nearest.distance;
            """;
        var embeddingParameter = command.Parameters.Add("@embedding", SqliteType.Blob);
        embeddingParameter.Value = ToBytes(embedding);
        command.Parameters.AddWithValue("@limit", CandidateLimit);

        using var reader = command.ExecuteReader();
        return ReadDistinctResults(reader, includeDistance: true);
    }

    private IReadOnlyList<SearchResult> ReadDistinctResults(SqliteDataReader reader, bool includeDistance)
    {
        var results = new List<SearchResult>();
        var paths = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read() && results.Count < ResultLimit)
        {
            var path = reader.GetString(0);
            if (!paths.Add(path))
            {
                continue;
            }

            double? distance = includeDistance ? reader.GetDouble(2) : null;
            results.Add(new SearchResult(path, ToSnippet(reader.GetString(1)), distance));
        }

        return results;
    }

    private static void DeleteChunksForPath(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        var ids = new List<long>();
        using (var idsCommand = connection.CreateCommand())
        {
            idsCommand.Transaction = transaction;
            idsCommand.CommandText = "SELECT id FROM note_chunks WHERE path = @path;";
            idsCommand.Parameters.AddWithValue("@path", path);
            using var reader = idsCommand.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            ExecuteNonQuery(connection, transaction, "DELETE FROM note_chunks_fts WHERE rowid = @id;", ("@id", id));
            ExecuteNonQuery(connection, transaction, "DELETE FROM note_embeddings WHERE rowid = @id;", ("@id", id));
            ExecuteNonQuery(connection, transaction, "DELETE FROM embedded_chunks WHERE chunk_id = @id;", ("@id", id));
        }

        ExecuteNonQuery(connection, transaction, "DELETE FROM note_chunks WHERE path = @path;", ("@path", path));
    }

    private static void InsertChunk(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        int chunkOrder,
        string content)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO note_chunks(path, chunk_order, content) VALUES (@path, @chunkOrder, @content);";
        insert.Parameters.AddWithValue("@path", path);
        insert.Parameters.AddWithValue("@chunkOrder", chunkOrder);
        insert.Parameters.AddWithValue("@content", content);
        insert.ExecuteNonQuery();

        using var lastId = connection.CreateCommand();
        lastId.Transaction = transaction;
        lastId.CommandText = "SELECT last_insert_rowid();";
        var id = Convert.ToInt64(lastId.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        ExecuteNonQuery(connection, transaction, "INSERT INTO note_chunks_fts(rowid, content) VALUES (@id, @content);", ("@id", id), ("@content", content));
    }

    private static void InsertEmbedding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long chunkId,
        float[] embedding)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO note_embeddings(rowid, embedding) VALUES (@id, @embedding);";
        insert.Parameters.AddWithValue("@id", chunkId);
        var embeddingParameter = insert.Parameters.Add("@embedding", SqliteType.Blob);
        embeddingParameter.Value = ToBytes(embedding);
        insert.ExecuteNonQuery();

        ExecuteNonQuery(connection, transaction, "INSERT INTO embedded_chunks(chunk_id) VALUES (@id);", ("@id", chunkId));
    }

    private static void UpsertIndexedFile(SqliteConnection connection, SqliteTransaction transaction, string path, string hash)
    {
        ExecuteNonQuery(
            connection,
            transaction,
            """
            INSERT INTO indexed_files(path, content_hash) VALUES (@path, @hash)
            ON CONFLICT(path) DO UPDATE SET content_hash = excluded.content_hash;
            """,
            ("@path", path),
            ("@hash", hash));
    }

    private static void DeleteIndexedFile(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        ExecuteNonQuery(connection, transaction, "DELETE FROM indexed_files WHERE path = @path;", ("@path", path));
    }

    private static Dictionary<string, string> GetIndexedFiles(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, content_hash FROM indexed_files;";
        using var reader = command.ExecuteReader();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            files[reader.GetString(0)] = reader.GetString(1);
        }

        return files;
    }

    private static List<IndexedChunk> GetChunksWithoutEmbeddings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunks.id, chunks.content
            FROM note_chunks AS chunks
            LEFT JOIN embedded_chunks AS embedded ON embedded.chunk_id = chunks.id
            WHERE embedded.chunk_id IS NULL
            ORDER BY chunks.id;
            """;
        using var reader = command.ExecuteReader();
        var chunks = new List<IndexedChunk>();
        while (reader.Read())
        {
            chunks.Add(new IndexedChunk(reader.GetInt64(0), reader.GetString(1)));
        }

        return chunks;
    }

    private static string? GetMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM search_metadata WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetMetadata(SqliteConnection connection, string key, string value)
    {
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO search_metadata(key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """,
            ("@key", key),
            ("@value", value));
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static IEnumerable<string> Chunk(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Trim();
        for (var start = 0; start < normalized.Length;)
        {
            var end = Math.Min(start + ChunkSize, normalized.Length);
            if (end < normalized.Length)
            {
                var lineBreak = normalized.LastIndexOf('\n', end - 1, end - start);
                if (lineBreak > start + (ChunkSize / 2))
                {
                    end = lineBreak + 1;
                }
            }

            var chunk = normalized[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                yield return chunk;
            }

            if (end == normalized.Length)
            {
                yield break;
            }

            start = Math.Max(end - ChunkOverlap, start + 1);
        }
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ToFtsMatchQuery(string query)
    {
        var terms = Regex.Matches(query, "[\\p{L}\\p{N}_]+")
            .Select(match => $"\"{match.Value.Replace("\"", "\"\"")}\"");
        return string.Join(" AND ", terms);
    }

    private static string ToSnippet(string content)
    {
        return content.Replace("\r\n", "\n").Trim();
    }

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private int ResultLimit => Math.Clamp(_options.SearchResultLimit, 1, 20);
    private int CandidateLimit => ResultLimit * 4;

    private sealed record IndexedFile(string Content, string Hash);
    private sealed record IndexedChunk(long Id, string Content);
}
