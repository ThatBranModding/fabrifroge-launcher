using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace FabriFrogeLauncher;

public static class Net
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static HttpClient CreateHttpClient()
    {
        // Handles gzip/br etc automatically
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    public static async Task<T> GetJsonAsync<T>(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);

        // Handle BOM safely
        json = json.TrimStart('\uFEFF');

        var obj = JsonSerializer.Deserialize<T>(json, JsonOpts);
        if (obj == null) throw new Exception($"Failed to parse JSON from {url}");
        return obj;
    }
}
