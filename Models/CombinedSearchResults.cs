namespace ObsidianBot.Models;

public sealed record CombinedSearchResults(
    IReadOnlyList<SearchResult> FullText,
    IReadOnlyList<SearchResult> Semantic,
    bool SemanticSearchConfigured);
