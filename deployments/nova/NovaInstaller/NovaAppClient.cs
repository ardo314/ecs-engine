using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NovaInstaller;

/// <summary>
/// Talks to the NOVA app endpoints under <c>/api/v2/cells/{cell}/apps</c>.
/// NOVA exposes no direct Kubernetes access, so this REST API is the only way
/// to place workloads on an instance.
/// </summary>
public sealed class NovaAppClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _appsPath;

    public NovaAppClient(string baseUrl, string cell, string? accessToken, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _appsPath = $"api/v2/cells/{Uri.EscapeDataString(cell)}/apps";

        if (!string.IsNullOrWhiteSpace(accessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    /// <summary>Names of the apps currently installed in the cell.</summary>
    public async Task<IReadOnlyList<string>> ListAppNamesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(_appsPath, ct);
        await EnsureSuccessAsync(response, "list apps", ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        return ExtractNames(document.RootElement);
    }

    public async Task AddAppAsync(AppManifest manifest, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(_appsPath, manifest, NovaJson.Options, ct);
        await EnsureSuccessAsync(response, $"install app '{manifest.Name}'", ct);
    }

    /// <summary>Returns false when the app was already absent.</summary>
    public async Task<bool> DeleteAppAsync(string name, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"{_appsPath}/{Uri.EscapeDataString(name)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;

        await EnsureSuccessAsync(response, $"delete app '{name}'", ct);
        return true;
    }

    /// <summary>
    /// Deletion is asynchronous on the instance, so reinstalling immediately can
    /// collide with the outgoing app.
    /// </summary>
    public async Task WaitUntilAbsentAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var names = await ListAppNamesAsync(ct);
            if (!names.Contains(name, StringComparer.Ordinal)) return;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new NovaApiException($"App '{name}' was still present {timeout.TotalSeconds:0}s after deletion.");
    }

    /// <summary>
    /// The list response shape is not pinned by the public docs, so accept both a
    /// bare array and an object wrapping one.
    /// </summary>
    private static List<string> ExtractNames(JsonElement root)
    {
        var array = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when TryFindArray(root, out var found) => found,
            _ => default
        };

        if (array.ValueKind != JsonValueKind.Array) return [];

        return array
            .EnumerateArray()
            .Select(element => element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object when element.TryGetProperty("name", out var name) => name.GetString(),
                _ => null
            })
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

    private static bool TryFindArray(JsonElement root, out JsonElement array)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) continue;
            array = property.Value;
            return true;
        }

        array = default;
        return false;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new NovaApiException($"Failed to {action}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}".TrimEnd());
    }

    public void Dispose() => _http.Dispose();
}

public sealed class NovaApiException(string message) : Exception(message);
