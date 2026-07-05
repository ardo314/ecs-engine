using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nova.Systems;

/// <summary>
/// HTTP client wrapper for the Wandelbots Nova controller IO API.
/// Calls PUT /cells/{cell}/controllers/{controller}/ios/values
/// </summary>
public sealed class NovaIoClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NovaIoClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public void SetAuthToken(string token)
    {
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Sets output values on a controller via the Nova API.
    /// PUT /api/v2/cells/{cell}/controllers/{controller}/ios/values
    /// </summary>
    public async Task<bool> SetOutputValuesAsync(
        string cell,
        string controller,
        IReadOnlyList<IoValuePayload> values,
        CancellationToken ct = default)
    {
        var url = $"/api/v2/cells/{Uri.EscapeDataString(cell)}/controllers/{Uri.EscapeDataString(controller)}/ios/values";
        var response = await _http.PutAsJsonAsync(url, values, JsonOptions, ct);
        return response.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Payload for a single IO value to send to the Nova API.
/// Discriminated by value_type (boolean | integer | float).
/// </summary>
public record IoValuePayload
{
    [JsonPropertyName("io")]
    public required string Io { get; init; }

    [JsonPropertyName("value")]
    public required object Value { get; init; }

    [JsonPropertyName("value_type")]
    public required string ValueType { get; init; }

    public static IoValuePayload Boolean(string io, bool value) =>
        new() { Io = io, Value = value, ValueType = "boolean" };

    public static IoValuePayload Integer(string io, long value) =>
        new() { Io = io, Value = value.ToString(), ValueType = "integer" };

    public static IoValuePayload Float(string io, double value) =>
        new() { Io = io, Value = value, ValueType = "float" };
}
