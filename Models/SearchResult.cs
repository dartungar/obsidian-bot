namespace ObsidianBot.Models;

public sealed record SearchResult(string NotePath, string Snippet, double? Distance = null);
