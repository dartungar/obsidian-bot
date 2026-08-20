using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

public sealed class OpenAiEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly ObsidianBotOptions _options;

    public OpenAiEmbeddingClient(HttpClient httpClient, ObsidianBotOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.EmbeddingsApiKey);

    public async Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Semantic search is not configured. Set OPENAI_API_KEY first.");
        }

        if (inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.EmbeddingsApiUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    input = inputs,
                    model = _options.EmbeddingModel,
                    dimensions = _options.EmbeddingDimensions
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.EmbeddingsApiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var details = responseBody.Length > 500 ? responseBody[..500] : responseBody;
            throw new InvalidOperationException(
                $"Embedding request failed ({(int)response.StatusCode}): {details}");
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Embedding response did not contain a data array.");
        }

        var results = new float[inputs.Count][];
        foreach (var item in data.EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32();
            if (index < 0 || index >= results.Length)
            {
                throw new InvalidOperationException("Embedding response contained an invalid item index.");
            }

            var values = item.GetProperty("embedding");
            if (values.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Embedding response contained an invalid vector.");
            }

            var vector = values.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (vector.Length != _options.EmbeddingDimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding response has {vector.Length} dimensions; expected {_options.EmbeddingDimensions}.");
            }

            results[index] = vector;
        }

        if (results.Any(vector => vector is null))
        {
            throw new InvalidOperationException("Embedding response omitted one or more inputs.");
        }

        return results!;
    }
}
