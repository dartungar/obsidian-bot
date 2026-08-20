using System.Text;
using System.Text.RegularExpressions;
using ObsidianBot.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ObsidianBot.Telegram;

public sealed record FormattedTelegramMessage(string Text, IReadOnlyList<MessageEntity> Entities);

public static class SearchResultMessageFormatter
{
    private const int MessageLimit = 3_900;

    public static FormattedTelegramMessage FormatCombined(CombinedSearchResults results)
    {
        var builder = new EntityTextBuilder(MessageLimit);
        AppendSection(builder, "Full-text results", results.FullText, semantic: false, snippetLength: 180);
        builder.Append("\n\n");

        if (results.SemanticSearchConfigured)
        {
            AppendSection(builder, "Semantic results", results.Semantic, semantic: true, snippetLength: 180);
        }
        else
        {
            builder.AppendEntity("Semantic results", MessageEntityType.Bold);
            builder.Append(":\nSemantic search is not configured. Set OPENAI_API_KEY first.");
        }

        return builder.Build();
    }

    public static FormattedTelegramMessage FormatSemantic(IReadOnlyList<SearchResult> results)
    {
        var builder = new EntityTextBuilder(MessageLimit);
        AppendSection(builder, "Semantic results", results, semantic: true, snippetLength: 320);
        return builder.Build();
    }

    private static void AppendSection(
        EntityTextBuilder builder,
        string title,
        IReadOnlyList<SearchResult> results,
        bool semantic,
        int snippetLength)
    {
        builder.AppendEntity(title, MessageEntityType.Bold);
        builder.Append(":\n");

        if (results.Count == 0)
        {
            builder.Append("No matching notes found.");
            return;
        }

        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            builder.Append("\n");
            builder.AppendEntity(Truncate(result.NotePath, 180), MessageEntityType.Underline);
            builder.Append("\n");
            AppendMarkdownSnippet(builder, PrepareSnippet(result), snippetLength);

            if (semantic && result.Distance is { } distance)
            {
                builder.Append($"\nDistance: {distance:F3}");
            }

            if (index < results.Count - 1)
            {
                builder.Append("\n\n");
            }
        }
    }

    private static string PrepareSnippet(SearchResult result)
    {
        var content = result.Snippet.Replace("\r\n", "\n");
        var indexingTitle = Path.GetFileNameWithoutExtension(result.NotePath);
        var indexingPrefix = indexingTitle + "\n\n";
        if (content.StartsWith(indexingPrefix, StringComparison.Ordinal))
        {
            content = content[indexingPrefix.Length..];
        }

        return StripYamlFrontmatter(content).Trim();
    }

    private static string StripYamlFrontmatter(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            return content;
        }

        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line is "---" or "...")
            {
                return string.Join("\n", lines[(index + 1)..]);
            }
        }

        return content;
    }

    private static void AppendMarkdownSnippet(EntityTextBuilder builder, string content, int limit)
    {
        var snippet = Truncate(content, limit);
        if (string.IsNullOrWhiteSpace(snippet))
        {
            builder.Append("(No displayable note content.)");
            return;
        }

        var lines = snippet.Replace("\r\n", "\n").Split('\n');
        var inCodeBlock = false;
        var codeStart = 0;
        string? codeLanguage = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (TryGetFence(line, out var language))
            {
                if (inCodeBlock)
                {
                    builder.AddEntity(MessageEntityType.Pre, codeStart, builder.Length, language: codeLanguage);
                    inCodeBlock = false;
                    codeLanguage = null;
                }
                else
                {
                    inCodeBlock = true;
                    codeStart = builder.Length;
                    codeLanguage = language;
                }

                continue;
            }

            if (inCodeBlock)
            {
                builder.Append(line);
            }
            else
            {
                AppendMarkdownLine(builder, line);
            }

            if (index < lines.Length - 1)
            {
                builder.Append("\n");
            }
        }

        if (inCodeBlock)
        {
            builder.AddEntity(MessageEntityType.Pre, codeStart, builder.Length, language: codeLanguage);
        }
    }

    private static bool TryGetFence(string line, out string? language)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) && !trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            language = null;
            return false;
        }

        language = trimmed.Length > 3 ? trimmed[3..].Trim() : null;
        return true;
    }

    private static void AppendMarkdownLine(EntityTextBuilder builder, string line)
    {
        var content = line;
        var isHeading = TryGetHeading(content, out var heading);
        if (isHeading)
        {
            var start = builder.Length;
            AppendInlineMarkdown(builder, heading);
            builder.AddEntity(MessageEntityType.Bold, start, builder.Length);
            return;
        }

        if (TryGetListItem(content, out var prefix, out var listContent))
        {
            builder.Append(prefix);
            AppendInlineMarkdown(builder, listContent);
            return;
        }

        AppendInlineMarkdown(builder, content);
    }

    private static bool TryGetHeading(string line, out string content)
    {
        var trimmed = line.TrimStart();
        var markerLength = 0;
        while (markerLength < trimmed.Length && trimmed[markerLength] == '#')
        {
            markerLength++;
        }

        if (markerLength is > 0 and <= 6 && markerLength < trimmed.Length && char.IsWhiteSpace(trimmed[markerLength]))
        {
            content = trimmed[markerLength..].Trim();
            return true;
        }

        content = string.Empty;
        return false;
    }

    private static bool TryGetListItem(string line, out string prefix, out string content)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- [ ] ", StringComparison.Ordinal) || trimmed.StartsWith("* [ ] ", StringComparison.Ordinal))
        {
            prefix = "☐ ";
            content = trimmed[6..];
            return true;
        }

        if (trimmed.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("* [x] ", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "☑ ";
            content = trimmed[6..];
            return true;
        }

        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && char.IsWhiteSpace(trimmed[1]))
        {
            prefix = "• ";
            content = trimmed[2..];
            return true;
        }

        var ordered = Regex.Match(trimmed, "^(\\d+[.)])\\s+(.+)$");
        if (ordered.Success)
        {
            prefix = ordered.Groups[1].Value + " ";
            content = ordered.Groups[2].Value;
            return true;
        }

        prefix = string.Empty;
        content = string.Empty;
        return false;
    }

    private static void AppendInlineMarkdown(EntityTextBuilder builder, string text)
    {
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                builder.Append(text[(index + 1)..(index + 2)]);
                index += 2;
                continue;
            }

            if (TryAppendWikiLink(builder, text, ref index) ||
                TryAppendMarkdownLink(builder, text, ref index) ||
                TryAppendDelimited(builder, text, ref index, "**", MessageEntityType.Bold) ||
                TryAppendDelimited(builder, text, ref index, "__", MessageEntityType.Underline) ||
                TryAppendDelimited(builder, text, ref index, "~~", MessageEntityType.Strikethrough) ||
                TryAppendDelimited(builder, text, ref index, "`", MessageEntityType.Code) ||
                TryAppendDelimited(builder, text, ref index, "*", MessageEntityType.Italic) ||
                TryAppendDelimited(builder, text, ref index, "_", MessageEntityType.Italic))
            {
                continue;
            }

            builder.Append(text[index].ToString());
            index++;
        }
    }

    private static bool TryAppendWikiLink(EntityTextBuilder builder, string text, ref int index)
    {
        if (!text.AsSpan(index).StartsWith("[[", StringComparison.Ordinal))
        {
            return false;
        }

        var closing = text.IndexOf("]]", index + 2, StringComparison.Ordinal);
        if (closing < 0)
        {
            return false;
        }

        var target = text[(index + 2)..closing];
        var separator = target.LastIndexOf('|');
        builder.Append(separator >= 0 ? target[(separator + 1)..] : target);
        index = closing + 2;
        return true;
    }

    private static bool TryAppendMarkdownLink(EntityTextBuilder builder, string text, ref int index)
    {
        if (text[index] != '[')
        {
            return false;
        }

        var labelEnd = text.IndexOf(']', index + 1);
        if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
        {
            return false;
        }

        var urlEnd = text.IndexOf(')', labelEnd + 2);
        if (urlEnd < 0)
        {
            return false;
        }

        var url = text[(labelEnd + 2)..urlEnd].Trim();
        var start = builder.Length;
        AppendInlineMarkdown(builder, text[(index + 1)..labelEnd]);
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            builder.AddEntity(MessageEntityType.TextLink, start, builder.Length, url: url);
        }

        index = urlEnd + 1;
        return true;
    }

    private static bool TryAppendDelimited(
        EntityTextBuilder builder,
        string text,
        ref int index,
        string delimiter,
        MessageEntityType type)
    {
        if (!text.AsSpan(index).StartsWith(delimiter, StringComparison.Ordinal))
        {
            return false;
        }

        var contentStart = index + delimiter.Length;
        var closing = text.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        if (closing < contentStart + 1)
        {
            return false;
        }

        var start = builder.Length;
        AppendInlineMarkdown(builder, text[contentStart..closing]);
        builder.AddEntity(type, start, builder.Length);
        index = closing + delimiter.Length;
        return true;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var end = value.LastIndexOfAny([' ', '\n', '\t'], maxLength - 1);
        if (end < maxLength / 2)
        {
            end = maxLength - 1;
        }

        if (end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return value[..end].TrimEnd() + "…";
    }

    private sealed class EntityTextBuilder
    {
        private readonly int _limit;
        private readonly StringBuilder _text = new();
        private readonly List<MessageEntity> _entities = [];

        public EntityTextBuilder(int limit)
        {
            _limit = limit;
        }

        public int Length => _text.Length;

        public void Append(string value)
        {
            if (_text.Length >= _limit || string.IsNullOrEmpty(value))
            {
                return;
            }

            var available = _limit - _text.Length;
            var length = Math.Min(available, value.Length);
            if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            {
                length--;
            }

            if (length > 0)
            {
                _text.Append(value, 0, length);
            }
        }

        public void AppendEntity(string value, MessageEntityType type, string? url = null, string? language = null)
        {
            var start = Length;
            Append(value);
            AddEntity(type, start, Length, url, language);
        }

        public void AddEntity(MessageEntityType type, int start, int end, string? url = null, string? language = null)
        {
            while (end > start && char.IsWhiteSpace(_text[end - 1]))
            {
                end--;
            }

            if (end <= start)
            {
                return;
            }

            _entities.Add(new MessageEntity
            {
                Type = type,
                Offset = start,
                Length = end - start,
                Url = url,
                Language = language
            });
        }

        public FormattedTelegramMessage Build()
        {
            return new FormattedTelegramMessage(_text.ToString(), _entities);
        }
    }
}
