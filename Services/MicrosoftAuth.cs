using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace CraftDeck.Services;

public class AuthResult
{
    public string AccessToken = "";
    public string UUID = "";
    public string Username = "";
    public string? RefreshToken;
}

public static class MicrosoftAuth
{
    private const string ClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
    private const string DeviceEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string Scope = "XboxLive.SignIn XboxLive.offline_access";

    public static async Task<AuthResult> LoginInBrowserAsync(IProgress<string>? ui = null,
        Action<string>? onCode = null, CancellationToken ct = default)
    {
        var start = Read(await Post(Http.Client, DeviceEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        }, ct), DeviceEndpoint).RootElement;
        if (!start.TryGetProperty("user_code", out var codeEl))
            throw new Exception("Microsoft wouldn't start the sign-in: " +
                                (start.TryGetProperty("error_description", out var ed) ? ed.GetString() : "no reason given"));

        var userCode = codeEl.GetString()!;
        var deviceCode = start.GetProperty("device_code").GetString()!;
        var url = start.TryGetProperty("verification_uri_complete", out var vc) && vc.ValueKind == JsonValueKind.String
            ? vc.GetString()! : start.GetProperty("verification_uri").GetString()!;
        var interval = start.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
        var expiresIn = start.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 900;

        onCode?.Invoke(userCode);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            throw new Exception("Couldn't open the browser. If it stays closed, go to microsoft.com/link and enter " + userCode);
        }
        ui?.Report("Sign in in the browser — code " + userCode);

        var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var tokens = Read(await Post(Http.Client, TokenEndpoint, new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = DeviceGrant,
                ["device_code"] = deviceCode,
            }, ct), TokenEndpoint).RootElement;

            if (tokens.TryGetProperty("access_token", out var at))
            {
                var res = await CompleteAsync(at.GetString()!, ct);
                if (tokens.TryGetProperty("refresh_token", out var rt)) res.RefreshToken = rt.GetString();
                return res;
            }

            switch (tokens.GetProperty("error").GetString())
            {
                case "authorization_pending" when DateTime.UtcNow < deadline:
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "authorization_declined":
                    throw new Exception("The sign-in was declined in the browser.");
                case "expired_token" or "authorization_pending":
                    throw new Exception("The code expired before the sign-in finished. Try again.");
                default:
                    throw new Exception("Microsoft: " +
                        (tokens.TryGetProperty("error_description", out var d) ? d.GetString() : tokens.GetProperty("error").GetString()));
            }
        }
    }

    public static async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var el = Read(await Post(Http.Client, TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        }, ct), TokenEndpoint).RootElement;
        if (!el.TryGetProperty("access_token", out var at))
            throw new Exception("Microsoft refresh failed — sign in again. " +
                                (el.TryGetProperty("error_description", out var ed) ? ed.GetString() : ""));
        var res = await CompleteAsync(at.GetString()!, ct);
        if (el.TryGetProperty("refresh_token", out var nrt)) res.RefreshToken = nrt.GetString();
        return res;
    }

    private static async Task<AuthResult> CompleteAsync(string at, CancellationToken ct)
    {
        var http = Http.Client;

        const string User = "https://user.auth.xboxlive.com/user/authenticate";
        var xauth = await PostXbl(User,
            "{\"Properties\":{\"AuthMethod\":\"RPS\",\"SiteName\":\"user.auth.xboxlive.com\",\"RpsTicket\":\"d=" + at +
            "\"},\"RelyingParty\":\"http://auth.xboxlive.com\",\"TokenType\":\"JWT\"}", ct);
        var xat = Need(xauth, User, "Token");
        var uhs = Read(xauth, User).RootElement
            .GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

        const string Xsts = "https://xsts.auth.xboxlive.com/xsts/authorize";
        var xsts = await PostXbl(Xsts,
            "{\"Properties\":{\"SandboxId\":\"RETAIL\",\"UserTokens\":[\"" + xat + "\"]},\"RelyingParty\":\"rp://api.minecraftservices.com/\",\"TokenType\":\"JWT\"}", ct);
        var xststoken = Need(xsts, Xsts, "Token");

        const string McLogin = "https://api.minecraftservices.com/authentication/login_with_xbox";
        var mc = await PostJson(http, McLogin,
            "{\"identityToken\":\"XBL3.0 x=" + uhs + ";" + xststoken + "\"}", ct);
        var mcToken = Need(mc, McLogin, "access_token");

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Add("Authorization", "Bearer " + mcToken);
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception("Minecraft Java Edition not found on this Microsoft account");
        var profile = Read(((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct)),
            "https://api.minecraftservices.com/minecraft/profile").RootElement;

        return new AuthResult
        {
            AccessToken = mcToken,
            UUID = profile.GetProperty("id").GetString()!,
            Username = profile.GetProperty("name").GetString()!,
        };
    }

    private static async Task<(int Status, string Body)> Post(HttpClient http, string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static async Task<(int Status, string Body)> PostJson(HttpClient http, string url, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static async Task<(int Status, string Body)> PostXbl(string url, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("x-xbl-contract-version", "1");
        using var resp = await Http.Client.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static JsonDocument Read((int Status, string Body) resp, string url)
    {
        var trimmed = resp.Body.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            throw new Exception(new Uri(url).Host + " answered " + resp.Status +
                                (trimmed.Length == 0 ? " with an empty body" : ": " + Cut(resp.Body)));
        return JsonDocument.Parse(resp.Body);
    }

    private static string Need((int Status, string Body) resp, string url, string prop)
    {
        var el = Read(resp, url).RootElement;
        if (!el.TryGetProperty(prop, out var v))
            throw new Exception(new Uri(url).Host + " answered " + resp.Status + " without " + prop + ": " + Cut(resp.Body));
        return v.GetString()!;
    }

    private static string Cut(string s) => s.Length <= 220 ? s : s[..220] + "…";
}
