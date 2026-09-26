using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReplayPad.Core;

/// <summary>Raised when Google no longer accepts the saved sign-in (revoked, expired).</summary>
public sealed class GoogleSignInRequiredException(string message) : Exception(message);

/// <summary>
/// Google sign-in for a desktop app — no backend. Standard OAuth "loopback"
/// flow with PKCE: the browser opens Google's consent page, Google
/// redirects back to a one-shot listener on 127.0.0.1, and the resulting
/// refresh token is stored on this PC only, encrypted with the Windows
/// account (DPAPI). Scope is drive.file: ReplayPad can only see the files
/// it created itself, never the rest of the user's Drive.
/// </summary>
public sealed class GoogleAuth
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string Scope = "https://www.googleapis.com/auth/drive.file";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReplayPad.GoogleAuth.v1");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private sealed class Stored
    {
        public string RefreshToken { get; set; } = "";
        public string? Email { get; set; }
    }

    private readonly object _lock = new();
    private string? _refreshToken;
    private string? _accessToken;
    private DateTime _accessExpiresUtc;

    public string? Email { get; private set; }
    public bool IsSignedIn => _refreshToken != null;

    /// <summary>The OAuth client baked in at build time (see google-oauth.props.example).</summary>
    public static string ClientId => BuildValue("GoogleClientId");
    private static string ClientSecret => BuildValue("GoogleClientSecret");
    public static bool IsConfigured => ClientId.Length > 0;

    private static string BuildValue(string key)
        => Assembly.GetExecutingAssembly()
               .GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";

    private static string TokenPath => Path.Combine(AppPaths.DataDir, "google-signin.dat");

    public GoogleAuth()
    {
        try
        {
            if (!File.Exists(TokenPath))
                return;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<Stored>(plain);
            if (!string.IsNullOrEmpty(stored?.RefreshToken))
            {
                _refreshToken = stored.RefreshToken;
                Email = stored.Email;
            }
        }
        catch (Exception ex)
        {
            // Unreadable (e.g. copied from another Windows account): just sign in again.
            Logger.Log("Saved Google sign-in unreadable: " + ex.Message);
        }
    }

    private void Persist()
    {
        var stored = new Stored { RefreshToken = _refreshToken ?? "", Email = Email };
        byte[] cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(stored), Entropy,
            DataProtectionScope.CurrentUser);
        string temp = TokenPath + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, TokenPath, overwrite: true);
    }

    /// <summary>
    /// Opens the browser for consent and waits for Google's redirect.
    /// Cancel the token to abort (e.g. the user closed the tab).
    /// </summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Google backup isn't set up in this build of ReplayPad.");

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port}/";

            string url = AuthEndpoint +
                "?client_id=" + Uri.EscapeDataString(ClientId) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                "&response_type=code" +
                "&scope=" + Uri.EscapeDataString(Scope) +
                "&code_challenge=" + challenge +
                "&code_challenge_method=S256" +
                "&state=" + state +
                "&access_type=offline" +
                "&prompt=consent";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            string code = await WaitForRedirectAsync(listener, state, ct);

            var tokens = await PostTokenAsync(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier
            }, ct);

            if (!tokens.TryGetProperty("refresh_token", out var refresh))
                throw new InvalidOperationException("Google didn't return a long-lived sign-in. Please try again.");

            lock (_lock)
            {
                _refreshToken = refresh.GetString();
                _accessToken = tokens.GetProperty("access_token").GetString();
                _accessExpiresUtc = DateTime.UtcNow.AddSeconds(tokens.GetProperty("expires_in").GetInt32());
            }
            Persist();
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Records the account's e-mail for display once Drive tells us who it is.</summary>
    public void SetEmail(string? email)
    {
        if (email == Email || !IsSignedIn)
            return;
        Email = email;
        try { Persist(); } catch (Exception ex) { Logger.Log("Could not save Google sign-in: " + ex.Message); }
    }

    /// <summary>
    /// Serves the browser's redirect(s) until the one carrying our state
    /// arrives (browsers may also ask for /favicon.ico first).
    /// </summary>
    private static async Task<string> WaitForRedirectAsync(TcpListener listener, string state, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            string requestLine = await ReadRequestLineAsync(stream, timeout.Token);

            // "GET /?code=...&state=... HTTP/1.1"
            string target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "";
            int q = target.IndexOf('?');
            var query = System.Web.HttpUtility.ParseQueryString(q >= 0 ? target[(q + 1)..] : "");

            if (query["state"] != state)
            {
                await RespondAsync(stream, 404, "Not found", timeout.Token);
                continue;
            }
            if (query["error"] is string error)
            {
                await RespondAsync(stream, 200, Page("Sign-in cancelled",
                    "ReplayPad was not given access. You can close this tab."), timeout.Token);
                throw new OperationCanceledException(error == "access_denied"
                    ? "Sign-in was cancelled."
                    : "Google reported: " + error);
            }
            string? code = query["code"];
            if (string.IsNullOrEmpty(code))
            {
                await RespondAsync(stream, 400, "Missing code", timeout.Token);
                continue;
            }
            await RespondAsync(stream, 200, Page("Signed in ✔",
                "ReplayPad can now back up to your Google Drive. You can close this tab and go back to the app."),
                timeout.Token);
            return code;
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();
        while (text.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (text.ToString().Contains("\r\n\r\n"))
                break;
        }
        string all = text.ToString();
        int end = all.IndexOf("\r\n", StringComparison.Ordinal);
        return end >= 0 ? all[..end] : all;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string body, CancellationToken ct)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                      $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(content, ct);
    }

    private static string Page(string title, string message) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>ReplayPad</title>" +
        "<style>body{font-family:Segoe UI,sans-serif;background:#17171B;color:#EDEDF2;display:flex;" +
        "align-items:center;justify-content:center;height:100vh;margin:0}div{text-align:center;max-width:420px}" +
        "h1{font-size:22px}p{color:#9A9AA6}</style></head><body><div>" +
        $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>";

    /// <summary>A valid access token, refreshed when needed.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        string? refreshToken;
        lock (_lock)
        {
            if (!forceRefresh && _accessToken != null && DateTime.UtcNow < _accessExpiresUtc.AddMinutes(-2))
                return _accessToken;
            refreshToken = _refreshToken;
        }
        if (refreshToken == null)
            throw new GoogleSignInRequiredException("Sign in with Google first.");

        JsonElement tokens;
        try
        {
            tokens = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            }, ct);
        }
        catch (GoogleSignInRequiredException)
        {
            ForgetLocally();
            throw;
        }

        lock (_lock)
        {
            _accessToken = tokens.GetProperty("access_token").GetString();
            _accessExpiresUtc = DateTime.UtcNow.AddSeconds(tokens.GetProperty("expires_in").GetInt32());
            return _accessToken!;
        }
    }

    private static async Task<JsonElement> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string error = "";
            try { error = JsonDocument.Parse(body).RootElement.GetProperty("error").GetString() ?? ""; } catch { }
            if (error == "invalid_grant")
                throw new GoogleSignInRequiredException(
                    "Your Google sign-in expired or was removed — please sign in again.");
            throw new HttpRequestException($"Google sign-in failed ({(int)response.StatusCode} {error}).");
        }
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Revokes access at Google (best-effort) and forgets it on this PC.</summary>
    public async Task SignOutAsync()
    {
        string? token = _refreshToken;
        ForgetLocally();
        if (token == null)
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Http.PostAsync(RevokeEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Log("Google revoke failed (signed out locally anyway): " + ex.Message);
        }
    }

    private void ForgetLocally()
    {
        lock (_lock)
        {
            _refreshToken = null;
            _accessToken = null;
            Email = null;
        }
        try { File.Delete(TokenPath); } catch { }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
