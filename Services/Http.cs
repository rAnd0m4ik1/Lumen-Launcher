using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using CraftDeck.Models.Minecraft;

namespace CraftDeck.Services;

public static class Http
{
    public static readonly HttpClient Client = Create(TimeSpan.FromSeconds(30));

    public static readonly HttpClient Stream = Create(Timeout.InfiniteTimeSpan);

    private static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 16,
        };
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lumen", "1.0"));
        c.Timeout = timeout;
        return c;
    }

    public static async Task<string> GetStringAsync(string url) =>
        await Client.GetStringAsync(url);

    public static async Task<T> GetJsonAsync<T>(string url)
    {
        using var s = await Client.GetStreamAsync(url);
        return (await JsonSerializer.DeserializeAsync<T>(s, Json.Options))!;
    }
}
