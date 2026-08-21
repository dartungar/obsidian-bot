using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class VaultNotesService
{
    private static readonly Regex HeadingPattern = new(
        "^(#{1,6})\\s+(.+?)\\s*#*\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FrontmatterKeyPattern = new(
        "^[A-Za-z][A-Za-z0-9_-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WikiLinkPattern = new(
        "\\[\\[([^\\]|#]+)(?:#[^\\]|]+)?(?:\\|[^\\]]+)?\\]\\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ObsidianBotOptions _options;
    private readonly VaultAccessPolicy _accessPolicy;
    private readonly VaultSearchService _search;
    private readonly OpenAiEmbeddingClient _embeddings;

    public VaultNotesService(
        ObsidianBotOptions options,
        VaultAccessPolicy accessPolicy,
        VaultSearchService search,
        OpenAiEmbeddingClient embeddings)
    {
        _options = options;
        _accessPolicy = accessPolicy;
        _search = search;
        _embeddings = embeddings;
    }

    public async Task<AgentSearchResponse> SearchAsync(
        string query,
        string mode,
        int limit,
        string? folder,
        string? tag,
        string? type,
        string? status,
        IReadOnlySet<string> includes,
        CancellationToken ct)
    {
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "hybrid" : mode.Trim().ToLowerInvariant();
        if (normalizedMode is not ("semantic" or "full_text" or "hybrid"))
        {
            throw new VaultApiException("mode must be one of: semantic, full_text, hybrid.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new VaultApiException("q is required.");
        }

        limit = Math.Clamp(limit, 1, 20);
        var ranked = await SearchRankedAsync(query.Trim(), normalizedMode, Math.Max(limit * 10, 100), ct);
        var results = new List<AgentNoteSearchResult>();
        foreach (var item in ranked)
        {
            if (!_accessPolicy.CanRead(item.NotePath) || !MatchesFolder(item.NotePath, folder))
            {
                continue;
            }

            var note = await ReadNoteAsync(item.NotePath, ct);
            if (note is null ||
                !MatchesMetadata(note, tag, type, status))
            {
                continue;
            }

            results.Add(ToSearchResponse(note, item.Score, item.Snippet, query, includes));
            if (results.Count == limit)
            {
                break;
            }
        }

        return new AgentSearchResponse(
            results,
            new AgentSearchMeta(query.Trim(), normalizedMode, GetWritableFolders()));
    }

    public async Task<VaultNote?> FindReadableNoteAsync(string noteId, CancellationToken ct)
    {
        foreach (var relativePath in EnumerateReadableNotePaths())
        {
            if (!string.Equals(GetNoteId(relativePath), noteId, StringComparison.Ordinal))
            {
                continue;
            }

            return await ReadNoteAsync(relativePath, ct);
        }

        return null;
    }

    public async Task<VaultNote?> FindWritableNoteAsync(string noteId, CancellationToken ct)
    {
        var note = await FindReadableNoteAsync(noteId, ct);
        return note is not null && _accessPolicy.CanWrite(note.Path) ? note : null;
    }

    public AgentNoteResponse ToNoteResponse(VaultNote note, IReadOnlySet<string> includes)
    {
        return new AgentNoteResponse(
            note.Id,
            note.Path,
            note.Title,
            note.Revision,
            includes.Contains("frontmatter") ? note.Frontmatter : null,
            includes.Contains("headings") ? note.Sections.Select(ToSectionSummary).ToArray() : null,
            includes.Contains("content") ? note.Content : null,
            GetNotePolicy(note));
    }

    public AgentSectionResponse? GetSectionResponse(VaultNote note, string sectionId, int contextLines)
    {
        var section = note.Sections.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, sectionId, StringComparison.Ordinal));
        if (section is null)
        {
            return null;
        }

        contextLines = Math.Clamp(contextLines, 0, 20);
        var beforeStart = Math.Max(0, section.HeadingLineIndex - contextLines);
        var afterEnd = Math.Min(note.Lines.Count, section.EndLineIndex + contextLines);
        return new AgentSectionResponse(
            section.Id,
            section.HeadingPath,
            note.Revision,
            Slice(note.Content, section.ContentStartOffset, section.EndOffset).Trim(),
            JoinLines(note, beforeStart, section.HeadingLineIndex + 1).TrimEnd(),
            JoinLines(note, section.EndLineIndex, afterEnd).Trim());
    }

    public async Task<AgentLinksResponse> GetLinksAsync(VaultNote note, string direction, int limit, CancellationToken ct)
    {
        var normalizedDirection = string.IsNullOrWhiteSpace(direction) ? "both" : direction.Trim().ToLowerInvariant();
        if (normalizedDirection is not ("backlinks" or "outgoing" or "both"))
        {
            throw new VaultApiException("direction must be one of: backlinks, outgoing, both.");
        }

        limit = Math.Clamp(limit, 1, 100);
        var outgoing = normalizedDirection is "outgoing" or "both"
            ? await GetOutgoingLinksAsync(note, limit, ct)
            : Array.Empty<NoteLinkReference>();
        var backlinks = normalizedDirection is "backlinks" or "both"
            ? await GetBacklinksAsync(note, limit, ct)
            : Array.Empty<NoteLinkReference>();

        return new AgentLinksResponse(
            backlinks,
            outgoing,
            Array.Empty<NoteLinkReference>(),
            note.Tags,
            GetNotePolicy(note));
    }

    public NotePolicy GetNotePolicy(VaultNote note)
    {
        var appendableSections = note.Sections
            .Where(section => CanDirectAppend(note, section, isTask: false))
            .ToArray();
        var operations = new List<string>();
        if (appendableSections.Length > 0)
        {
            operations.Add("append_section");
        }

        if (appendableSections.Any(section => CanDirectAppend(note, section, isTask: true)))
        {
            operations.Add("append_task");
        }

        return new NotePolicy(
            Readable: _accessPolicy.CanRead(note.Path),
            DirectOperations: operations,
            AllowedSectionIds: appendableSections.Select(section => section.Id).ToArray(),
            RequiresReviewFor: ["replace_section", "set_frontmatter"]);
    }

    public bool CanDirectAppend(VaultNote note, VaultSection section, bool isTask)
    {
        if (!_accessPolicy.CanWrite(note.Path) || section.HeadingPath.Count == 0)
        {
            return false;
        }

        var heading = section.HeadingPath[^1];
        return _options.AgentDirectAllowedHeadings.Contains(heading, StringComparer.OrdinalIgnoreCase) &&
               (!isTask || string.Equals(heading, "Tasks", StringComparison.OrdinalIgnoreCase));
    }

    public bool TryResolveWritableDestination(
        string? folderId,
        string? filename,
        out string relativePath,
        out string fullPath,
        out string error)
    {
        relativePath = string.Empty;
        fullPath = string.Empty;
        error = string.Empty;
        var folder = _accessPolicy.GetWritableFolders().SingleOrDefault(candidate =>
            string.Equals(candidate.Id, folderId, StringComparison.Ordinal));
        if (folder is null)
        {
            error = "destination.folder_id is not an allowed writable folder.";
            return false;
        }

        var cleanFilename = filename?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanFilename) ||
            !string.Equals(cleanFilename, Path.GetFileName(cleanFilename), StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(cleanFilename), ".md", StringComparison.OrdinalIgnoreCase) ||
            cleanFilename.StartsWith(".", StringComparison.Ordinal))
        {
            error = "destination.filename must be a Markdown filename without path separators.";
            return false;
        }

        relativePath = string.IsNullOrEmpty(folder.Path) ? cleanFilename : $"{folder.Path}/{cleanFilename}";
        if (!_accessPolicy.CanWrite(relativePath))
        {
            error = "The requested destination is not writable by policy.";
            return false;
        }

        fullPath = Path.GetFullPath(Path.Combine(_options.VaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInsideVault(fullPath))
        {
            error = "The requested destination is outside the vault.";
            return false;
        }

        return true;
    }

    public bool TryBuildNewNoteContent(
        Dictionary<string, JsonElement>? frontmatter,
        string markdown,
        out string content,
        out string error)
    {
        content = string.Empty;
        error = string.Empty;
        var body = markdown.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            error = "content_markdown is required.";
            return false;
        }

        if (frontmatter is null || frontmatter.Count == 0)
        {
            content = body + "\n";
            return true;
        }

        var lines = new List<string> { "---" };
        foreach (var (key, value) in frontmatter.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!FrontmatterKeyPattern.IsMatch(key))
            {
                error = $"frontmatter key '{key}' is invalid.";
                return false;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    lines.Add($"{key}: {JsonSerializer.Serialize(value.GetString())}");
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    lines.Add($"{key}: {value.GetRawText()}");
                    break;
                case JsonValueKind.Array:
                    lines.Add($"{key}:");
                    foreach (var entry in value.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.String)
                        {
                            error = $"frontmatter array '{key}' may contain only strings.";
                            return false;
                        }

                        lines.Add($"  - {JsonSerializer.Serialize(entry.GetString())}");
                    }

                    break;
                default:
                    error = $"frontmatter value '{key}' must be a string, number, boolean, or string array.";
                    return false;
            }
        }

        lines.Add("---");
        lines.Add(string.Empty);
        content = string.Join("\n", lines) + "\n" + body + "\n";
        return true;
    }

    public bool TryAppendToSection(VaultNote note, string sectionId, string markdown, out string content, out string error)
    {
        content = string.Empty;
        error = string.Empty;
        var section = note.Sections.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, sectionId, StringComparison.Ordinal));
        if (section is null)
        {
            error = "The target section no longer resolves exactly once.";
            return false;
        }

        var addition = markdown.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(addition))
        {
            error = "content_markdown is required.";
            return false;
        }

        var newline = note.Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        addition = addition.Replace("\n", newline);
        var prefix = note.Content[..section.EndOffset];
        var suffix = note.Content[section.EndOffset..];
        var stablePrefix = prefix.TrimEnd('\r', '\n');
        var trailingLineBreaks = prefix[stablePrefix.Length..];
        content = stablePrefix + newline + addition +
                  (trailingLineBreaks.Length == 0 ? newline : trailingLineBreaks) + suffix;
        return true;
    }

    public string GetNoteId(string relativePath) => "note_" + ShortHash("note\n" + NormalizePath(relativePath));

    public string GetRevision(string content) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public IReadOnlyList<WritableFolder> GetWritableFolders() => _accessPolicy.GetWritableFolders()
        .Where(folder => Directory.Exists(Path.Combine(
            _options.VaultPath,
            folder.Path.Replace('/', Path.DirectorySeparatorChar))))
        .ToArray();

    private async Task<IReadOnlyList<RankedSearchResult>> SearchRankedAsync(
        string query,
        string mode,
        int candidateLimit,
        CancellationToken ct)
    {
        if (mode == "full_text")
        {
            var results = await _search.SearchFullTextAsync(query, candidateLimit, ct);
            return results.Select((result, index) => new RankedSearchResult(
                result.NotePath,
                1 - (index * 0.01),
                result.Snippet)).ToArray();
        }

        if (mode == "semantic")
        {
            if (!_embeddings.IsConfigured)
            {
                throw new VaultApiException("Semantic search is not configured. Set OPENAI_API_KEY first.");
            }

            var results = await _search.SearchSemanticAsync(query, candidateLimit, ct);
            return results.Select((result, index) => new RankedSearchResult(
                result.NotePath,
                Math.Max(0, 1 - (result.Distance ?? (index / 100d))),
                result.Snippet)).ToArray();
        }

        var combined = await _search.SearchCombinedAsync(query, candidateLimit, ct);
        var ranked = new Dictionary<string, RankedSearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (result, index) in combined.FullText.Select((result, index) => (result, index)))
        {
            ranked[result.NotePath] = new RankedSearchResult(result.NotePath, 1 - (index * 0.01), result.Snippet);
        }

        foreach (var (result, index) in combined.Semantic.Select((result, index) => (result, index)))
        {
            var score = Math.Max(0, 1 - (result.Distance ?? (index / 100d)));
            if (!ranked.TryGetValue(result.NotePath, out var current) || score > current.Score)
            {
                ranked[result.NotePath] = new RankedSearchResult(result.NotePath, score, result.Snippet);
            }
        }

        return ranked.Values.OrderByDescending(result => result.Score).ToArray();
    }

    private AgentNoteSearchResult ToSearchResponse(
        VaultNote note,
        double score,
        string snippet,
        string query,
        IReadOnlySet<string> includes)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matchingSections = note.Sections
            .Where(section => terms.Any(term =>
                Slice(note.Content, section.ContentStartOffset, section.EndOffset)
                    .Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .Select(ToSectionSummary)
            .ToArray();

        return new AgentNoteSearchResult(
            note.Id,
            note.Path,
            note.Title,
            note.Aliases,
            Math.Round(score, 4),
            includes.Contains("snippet") || includes.Count == 0 ? snippet : null,
            matchingSections,
            note.Revision,
            includes.Contains("frontmatter") ? note.Frontmatter : null,
            includes.Contains("headings") ? note.Sections.Select(ToSectionSummary).ToArray() : null,
            GetNotePolicy(note));
    }

    private async Task<IReadOnlyList<NoteLinkReference>> GetOutgoingLinksAsync(VaultNote note, int limit, CancellationToken ct)
    {
        var allNotes = await ReadAllReadableNotesAsync(ct);
        var byTitle = allNotes
            .SelectMany(candidate => new[] { candidate.Title }.Concat(candidate.Aliases)
                .Select(name => (Name: name, Note: candidate)))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Note, StringComparer.OrdinalIgnoreCase);

        return WikiLinkPattern.Matches(note.Content)
            .Select(match => match.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(link => byTitle.TryGetValue(link, out _))
            .Select(link => byTitle[link])
            .Take(limit)
            .Select(target => new NoteLinkReference(target.Id, target.Path, null))
            .ToArray();
    }

    private async Task<IReadOnlyList<NoteLinkReference>> GetBacklinksAsync(VaultNote target, int limit, CancellationToken ct)
    {
        var alternatives = new HashSet<string>(target.Aliases.Append(target.Title), StringComparer.OrdinalIgnoreCase);
        var result = new List<NoteLinkReference>();
        foreach (var note in await ReadAllReadableNotesAsync(ct))
        {
            if (string.Equals(note.Id, target.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var match = WikiLinkPattern.Matches(note.Content)
                .FirstOrDefault(candidate => alternatives.Contains(candidate.Groups[1].Value.Trim()));
            if (match is null)
            {
                continue;
            }

            result.Add(new NoteLinkReference(note.Id, note.Path, SnippetAround(note.Content, match.Index)));
            if (result.Count == limit)
            {
                break;
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<VaultNote>> ReadAllReadableNotesAsync(CancellationToken ct)
    {
        var notes = new List<VaultNote>();
        foreach (var relativePath in EnumerateReadableNotePaths())
        {
            var note = await ReadNoteAsync(relativePath, ct);
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        return notes;
    }

    private IEnumerable<string> EnumerateReadableNotePaths()
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_options.VaultPath, "*.md", SearchOption.AllDirectories);
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var fullPath in files)
        {
            var relativePath = NormalizePath(Path.GetRelativePath(_options.VaultPath, fullPath));
            if (_accessPolicy.CanRead(relativePath))
            {
                yield return relativePath;
            }
        }
    }

    private async Task<VaultNote?> ReadNoteAsync(string relativePath, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_options.VaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInsideVault(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(fullPath, ct);
        var frontmatter = ParseFrontmatter(content, out var bodyStartOffset);
        var title = GetString(frontmatter, "title") ?? Path.GetFileNameWithoutExtension(relativePath);
        var lines = SplitLines(content);
        var sections = ParseSections(content, lines, bodyStartOffset, GetNoteId(relativePath));
        return new VaultNote(
            GetNoteId(relativePath),
            relativePath,
            fullPath,
            title,
            GetStringList(frontmatter, "aliases"),
            GetStringList(frontmatter, "tags"),
            frontmatter,
            content,
            GetRevision(content),
            lines,
            sections);
    }

    private static IReadOnlyDictionary<string, object?> ParseFrontmatter(string content, out int bodyStartOffset)
    {
        bodyStartOffset = 0;
        var lines = SplitLines(content);
        if (lines.Count == 0 || !string.Equals(lines[0].Text.Trim(), "---", StringComparison.Ordinal))
        {
            return new Dictionary<string, object?>();
        }

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string? activeListKey = null;
        for (var index = 1; index < lines.Count; index++)
        {
            var raw = lines[index].Text;
            if (string.Equals(raw.Trim(), "---", StringComparison.Ordinal))
            {
                bodyStartOffset = lines[index].EndOffset;
                return values;
            }

            var trimmed = raw.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && activeListKey is not null)
            {
                if (values[activeListKey] is List<string> list)
                {
                    list.Add(Unquote(trimmed[2..].Trim()));
                }

                continue;
            }

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                activeListKey = null;
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                values[key] = new List<string>();
                activeListKey = key;
            }
            else
            {
                values[key] = ParseFrontmatterValue(value);
                activeListKey = null;
            }
        }

        return new Dictionary<string, object?>();
    }

    private static object ParseFrontmatterValue(string value)
    {
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return value[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Unquote)
                .ToList();
        }

        return Unquote(value);
    }

    private static IReadOnlyList<VaultLine> SplitLines(string content)
    {
        var lines = new List<VaultLine>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != '\n')
            {
                continue;
            }

            var textEnd = index > start && content[index - 1] == '\r' ? index - 1 : index;
            lines.Add(new VaultLine(start, textEnd, index + 1, content[start..textEnd]));
            start = index + 1;
        }

        if (start < content.Length || content.Length == 0)
        {
            lines.Add(new VaultLine(start, content.Length, content.Length, content[start..]));
        }

        return lines;
    }

    private static IReadOnlyList<VaultSection> ParseSections(
        string content,
        IReadOnlyList<VaultLine> lines,
        int bodyStartOffset,
        string noteId)
    {
        var headings = new List<MutableSection>();
        var hierarchy = new List<(int Level, string Name)>();
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
        {
            if (line.StartOffset < bodyStartOffset)
            {
                continue;
            }

            var match = HeadingPattern.Match(line.Text);
            if (!match.Success)
            {
                continue;
            }

            var level = match.Groups[1].Value.Length;
            var name = match.Groups[2].Value.Trim();
            while (hierarchy.Count > 0 && hierarchy[^1].Level >= level)
            {
                hierarchy.RemoveAt(hierarchy.Count - 1);
            }

            hierarchy.Add((level, name));
            headings.Add(new MutableSection(
                level,
                hierarchy.Select(item => item.Name).ToArray(),
                index,
                line.EndOffset));
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var sections = new List<VaultSection>();
        for (var index = 0; index < headings.Count; index++)
        {
            var current = headings[index];
            var endLineIndex = lines.Count;
            var endOffset = content.Length;
            for (var nextIndex = index + 1; nextIndex < headings.Count; nextIndex++)
            {
                if (headings[nextIndex].Level <= current.Level)
                {
                    endLineIndex = headings[nextIndex].HeadingLineIndex;
                    endOffset = lines[endLineIndex].StartOffset;
                    break;
                }
            }

            var pathKey = string.Join("\u001f", current.HeadingPath);
            occurrences.TryGetValue(pathKey, out var occurrence);
            occurrences[pathKey] = occurrence + 1;
            sections.Add(new VaultSection(
                "section_" + ShortHash($"{noteId}\n{pathKey}\n{occurrence}"),
                current.HeadingPath,
                current.Level,
                current.HeadingLineIndex,
                endLineIndex,
                current.ContentStartOffset,
                endOffset));
        }

        return sections;
    }

    private static bool MatchesFolder(string notePath, string? folder) =>
        string.IsNullOrWhiteSpace(folder) || VaultAccessPolicy.IsWithinFolder(notePath, folder);

    private static bool MatchesMetadata(VaultNote note, string? tag, string? type, string? status)
    {
        return (string.IsNullOrWhiteSpace(tag) || note.Tags.Any(value =>
                    string.Equals(value.TrimStart('#'), tag.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase))) &&
               (string.IsNullOrWhiteSpace(type) || string.Equals(
                    GetString(note.Frontmatter, "type"), type.Trim(), StringComparison.OrdinalIgnoreCase)) &&
               (string.IsNullOrWhiteSpace(status) || string.Equals(
                    GetString(note.Frontmatter, "status"), status.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool IsInsideVault(string fullPath) =>
        fullPath.StartsWith(_options.VaultPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        string.Equals(fullPath, _options.VaultPath, StringComparison.Ordinal);

    private static SectionSummary ToSectionSummary(VaultSection section) =>
        new(section.Id, section.HeadingPath, section.Level);

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value as string : null;

    private static IReadOnlyList<string> GetStringList(IReadOnlyDictionary<string, object?> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return Array.Empty<string>();
        }

        return value switch
        {
            string text => [text],
            IEnumerable<string> list => list.ToArray(),
            _ => Array.Empty<string>()
        };
    }

    private static string SnippetAround(string content, int index)
    {
        var start = Math.Max(0, index - 100);
        var end = Math.Min(content.Length, index + 200);
        return content[start..end].Replace("\r\n", "\n").Trim();
    }

    private static string JoinLines(VaultNote note, int start, int end) =>
        string.Concat(note.Lines.Skip(start).Take(Math.Max(0, end - start))
            .Select(line => note.Content[line.StartOffset..line.EndOffset]));

    private static string Slice(string content, int start, int end) =>
        start >= end ? string.Empty : content[start..end];

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
             (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim().Trim('/');

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private sealed record RankedSearchResult(string NotePath, double Score, string Snippet);

    private sealed record MutableSection(
        int Level,
        IReadOnlyList<string> HeadingPath,
        int HeadingLineIndex,
        int ContentStartOffset);
}

public sealed record VaultNote(
    string Id,
    string Path,
    string FullPath,
    string Title,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, object?> Frontmatter,
    string Content,
    string Revision,
    IReadOnlyList<VaultLine> Lines,
    IReadOnlyList<VaultSection> Sections);

public sealed record VaultLine(int StartOffset, int TextEndOffset, int EndOffset, string Text);

public sealed record VaultSection(
    string Id,
    IReadOnlyList<string> HeadingPath,
    int Level,
    int HeadingLineIndex,
    int EndLineIndex,
    int ContentStartOffset,
    int EndOffset);

public sealed class VaultApiException : Exception
{
    public VaultApiException(string message) : base(message)
    {
    }
}
