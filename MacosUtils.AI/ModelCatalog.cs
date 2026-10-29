using System.Net.Http.Headers;
using System.Text.Json;

namespace MacosUtils.AI;

/// <summary>
/// Lists the models an OpenAI-compatible endpoint offers (GET {endpoint}/models).
/// Parses the JSON loosely instead of using the OpenAI SDK's model client, because
/// compatible servers (Ollama, LM Studio, vLLM, OpenRouter...) often omit fields the SDK expects.
/// </summary>
public static class ModelCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Returns the model ids sorted by name. Throws on network/HTTP errors
    /// (<see cref="HttpRequestException"/> carries the status code for 401/403 detection).</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(
        string endpoint, string apiKey, CancellationToken cancellationToken = default)
    {
        endpoint = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint + "/models", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        apiKey = apiKey?.Trim() ?? "";
        // Local servers (Ollama, LM Studio) need no key; only send one when given.
        if (apiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The endpoint returned HTTP {(int)response.StatusCode} {response.ReasonPhrase} for /models.",
                null,
                response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseModelIds(document.RootElement);
    }

    // Accepts the OpenAI shape {"data":[{"id":...}]}, Ollama's {"models":[{"name":...}]}
    // and a bare array of objects or strings.
    private static List<string> ParseModelIds(JsonElement root)
    {
        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
            JsonValueKind.Object when root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array => models,
            _ => default,
        };
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (items.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var item in items.EnumerateArray())
        {
            string? id = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String => idProp.GetString(),
                JsonValueKind.Object when item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String => nameProp.GetString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        return [.. ids];
    }
}
