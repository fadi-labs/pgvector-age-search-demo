using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace BookSearchDemo.Services;

public class OpenRouterEmbeddingClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _httpReferer;
    private readonly string _xTitle;
    private readonly Random _random = new();

    public OpenRouterEmbeddingClient(IConfiguration config)
    {
        _baseUrl = config["OPENROUTER_BASE_URL"] ?? "https://openrouter.ai/api/v1";
        _model = config["OPENROUTER_EMBEDDING_MODEL"] ?? throw new InvalidOperationException("OPENROUTER_EMBEDDING_MODEL is not set");
        _apiKey = config["OPENROUTER_API_KEY"] ?? throw new InvalidOperationException("OPENROUTER_API_KEY is not set");
        _httpReferer = config["OPENROUTER_HTTP_REFERER"] ?? "http://localhost";
        _xTitle = config["OPENROUTER_X_TITLE"] ?? "PostgreSQL OpenRouter Book Demo";

        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", _httpReferer);
        _httpClient.DefaultRequestHeaders.Add("X-Title", _xTitle);
    }

    public async Task<List<float>> GetEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts == null || texts.Count == 0)
            return new List<float>();

        var payload = new
        {
            model = _model,
            input = texts
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        int attempt = 0;
        while (true)
        {
            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/embeddings",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) + _random.NextDouble());
                await Task.Delay(delay, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"OpenRouter request failed with status {response.StatusCode}: {body}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<EmbeddingResponse>(responseBody, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            if (result == null || result.Data == null)
                throw new InvalidOperationException("Unexpected OpenRouter response format");

            if (result.Data.Count != texts.Count)
                throw new InvalidOperationException($"Expected {texts.Count} embeddings but got {result.Data.Count}");

            var embeddings = new List<float>();
            foreach (var item in result.Data.OrderBy(d => d.Index))
            {
                embeddings.AddRange(item.Embedding);
            }

            return embeddings;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingDatum> Data { get; set; } = new();
    }

    private class EmbeddingDatum
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }
        [JsonPropertyName("embedding")]
        public List<float> Embedding { get; set; } = new();
    }
}
