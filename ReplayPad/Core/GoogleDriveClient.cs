using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReplayPad.Core;

/// <summary>
/// The few Google Drive REST calls backups need, straight over HTTPS
/// (no SDK, no backend). Backups live in a "ReplayPad Backups" folder in
/// the user's own Drive, tagged with appProperties so they're found again
/// from any PC signed into the same account.
/// </summary>
public sealed class GoogleDriveClient(GoogleAuth auth)
{
    private const string Api = "https://www.googleapis.com/drive/v3";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3";
    public const string FolderName = "ReplayPad Backups";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const int ChunkSize = 8 * 1024 * 1024; // must be a multiple of 256 KB

    // Resumable uploads answer 308 "Resume Incomplete"; never treat it as a redirect.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public sealed record BackupFile(string Id, string Name, long Size, DateTime CreatedUtc,
                                    string Machine, int Sounds, int Replays, int Categories);

    private string? _folderId;

    /// <summary>Sends with a fresh token; on 401 refreshes once and retries.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct,
                                                      HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        for (int attempt = 0; ; attempt++)
        {
            var request = make();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await auth.GetAccessTokenAsync(ct, forceRefresh: attempt > 0));
            var response = await Http.SendAsync(request, completion, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0)
                return response;
            response.Dispose();
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google Drive: {what} failed ({(int)response.StatusCode}). {ErrorMessage(body)}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string ErrorMessage(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? ""; }
        catch { return ""; }
    }

    private static string Q(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>The signed-in account's e-mail (for "Signed in as …").</summary>
    public async Task<string?> GetEmailAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Api}/about?fields=user(emailAddress)"), ct);
        var json = await ReadJsonAsync(response, "reading the account", ct);
        return json.TryGetProperty("user", out var user) && user.TryGetProperty("emailAddress", out var email)
            ? email.GetString()
            : null;
    }

    private async Task<string?> FindFolderAsync(CancellationToken ct)
    {
        if (_folderId != null)
            return _folderId;
        string q = $"name='{Q(FolderName)}' and mimeType='{FolderMime}' and trashed=false";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Api}/files?q={Uri.EscapeDataString(q)}&fields=files(id)&spaces=drive&pageSize=10"), ct);
        var json = await ReadJsonAsync(response, "finding the backup folder", ct);
        var files = json.GetProperty("files");
        return _folderId = files.GetArrayLength() > 0 ? files[0].GetProperty("id").GetString() : null;
    }

    private async Task<string> EnsureFolderAsync(CancellationToken ct)
    {
        if (await FindFolderAsync(ct) is string id)
            return id;
        string body = JsonSerializer.Serialize(new { name = FolderName, mimeType = FolderMime });
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Api}/files?fields=id")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, ct);
        var json = await ReadJsonAsync(response, "creating the backup folder", ct);
        return _folderId = json.GetProperty("id").GetString()!;
    }

    public async Task<List<BackupFile>> ListBackupsAsync(CancellationToken ct)
    {
        var result = new List<BackupFile>();
        if (await FindFolderAsync(ct) is not string folder)
            return result;

        string q = $"'{Q(folder)}' in parents and trashed=false and " +
                   "appProperties has { key='replaypad' and value='backup' }";
        string? pageToken = null;
        do
        {
            string url = $"{Api}/files?q={Uri.EscapeDataString(q)}&spaces=drive&pageSize=100" +
                         "&orderBy=createdTime desc" +
                         "&fields=nextPageToken,files(id,name,size,createdTime,appProperties)" +
                         (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            var json = await ReadJsonAsync(response, "listing backups", ct);
            foreach (var file in json.GetProperty("files").EnumerateArray())
                result.Add(ParseFile(file));
            pageToken = json.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        }
        while (pageToken != null);
        return result;
    }

    private static BackupFile ParseFile(JsonElement file)
    {
        string Prop(string key)
            => file.TryGetProperty("appProperties", out var props) && props.TryGetProperty(key, out var v)
                ? v.GetString() ?? ""
                : "";
        int Int(string key) => int.TryParse(Prop(key), out int n) ? n : 0;

        return new BackupFile(
            file.GetProperty("id").GetString()!,
            file.GetProperty("name").GetString() ?? "",
            file.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out long bytes) ? bytes : 0,
            file.TryGetProperty("createdTime", out var created) ? created.GetDateTime().ToUniversalTime() : DateTime.MinValue,
            Prop("machine"), Int("sounds"), Int("replays"), Int("categories"));
    }

    /// <summary>
    /// Resumable upload in 8 MB chunks; a dropped connection resumes from
    /// what Google already has instead of starting over.
    /// </summary>
    public async Task<BackupFile> UploadAsync(string zipPath, LibraryBackup.Manifest manifest,
                                              IProgress<double>? progress, CancellationToken ct)
    {
        string folder = await EnsureFolderAsync(ct);
        long total = new FileInfo(zipPath).Length;
        var local = manifest.CreatedUtc.ToLocalTime();

        string metadata = JsonSerializer.Serialize(new
        {
            name = $"ReplayPad backup {local:yyyy-MM-dd HH-mm} ({manifest.Machine}).zip",
            parents = new[] { folder },
            mimeType = "application/zip",
            description = $"ReplayPad library backup — {manifest.SoundCount} sounds, " +
                          $"{manifest.CategoryCount} categories, {manifest.ReplayCount} replays.",
            appProperties = new Dictionary<string, string>
            {
                ["replaypad"] = "backup",
                ["v"] = LibraryBackup.FormatVersion.ToString(),
                ["machine"] = Truncate(manifest.Machine, 60),
                ["sounds"] = manifest.SoundCount.ToString(),
                ["replays"] = manifest.ReplayCount.ToString(),
                ["categories"] = manifest.CategoryCount.ToString()
            }
        });

        Uri session;
        using (var start = await SendAsync(() =>
               {
                   var request = new HttpRequestMessage(HttpMethod.Post,
                       $"{UploadApi}/files?uploadType=resumable&fields=id,name,size,createdTime,appProperties")
                   {
                       Content = new StringContent(metadata, Encoding.UTF8, "application/json")
                   };
                   request.Headers.Add("X-Upload-Content-Type", "application/zip");
                   request.Headers.Add("X-Upload-Content-Length", total.ToString());
                   return request;
               }, ct))
        {
            if (!start.IsSuccessStatusCode || start.Headers.Location == null)
                await ReadJsonAsync(start, "starting the upload", ct);
            session = start.Headers.Location!;
        }

        var buffer = new byte[ChunkSize];
        long offset = 0;
        int failures = 0;
        await using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                file.Position = offset;
                int length = await file.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, total - offset)),
                    (int)Math.Min(ChunkSize, total - offset), throwOnEndOfStream: true, ct);

                using var response = await SendAsync(() =>
                {
                    var content = new ByteArrayContent(buffer, 0, length);
                    content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total);
                    return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
                }, ct);

                if (response.IsSuccessStatusCode)
                {
                    progress?.Report(1);
                    return ParseFile(await ReadJsonAsync(response, "finishing the upload", ct));
                }
                if ((int)response.StatusCode == 308)
                {
                    offset = CommittedBytes(response);
                    failures = 0;
                    progress?.Report((double)offset / total);
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    throw new InvalidOperationException("Google Drive: the upload session expired — please try again.");
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    // Permanent (e.g. Drive full): retrying won't help.
                    string body = await response.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException(
                        $"Google Drive refused the upload ({(int)response.StatusCode}). {ErrorMessage(body)}");
                }
                throw new HttpRequestException($"Google Drive is busy ({(int)response.StatusCode}).");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       && !ct.IsCancellationRequested
                                       && ++failures <= 5)
            {
                Logger.Log($"Upload hiccup ({failures}/5), resuming: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, failures)), ct);
                offset = await QueryCommittedAsync(session, total, ct) ?? offset;
            }
        }
    }

    /// <summary>How much Google has stored, from a 308's Range header ("bytes=0-N").</summary>
    private static long CommittedBytes(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Range", out var values) &&
            values.FirstOrDefault() is string range &&
            long.TryParse(range[(range.LastIndexOf('-') + 1)..], out long last))
            return last + 1;
        return 0;
    }

    private async Task<long?> QueryCommittedAsync(Uri session, long total, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(() =>
            {
                var content = new ByteArrayContent([]);
                content.Headers.ContentRange = new ContentRangeHeaderValue(total); // "bytes */total"
                return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
            }, ct);
            if ((int)response.StatusCode == 308)
                return CommittedBytes(response);
            if (response.IsSuccessStatusCode)
                return total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Log("Upload status check failed: " + ex.Message);
        }
        return null;
    }

    public async Task DownloadAsync(BackupFile backup, string targetPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Api}/files/{Uri.EscapeDataString(backup.Id)}?alt=media"), ct, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            await ReadJsonAsync(response, "downloading the backup", ct);

        long total = response.Content.Headers.ContentLength ?? backup.Size;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
                progress?.Report(Math.Min(1, (double)done / total));
        }
    }

    public async Task DeleteAsync(string fileId, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete,
            $"{Api}/files/{Uri.EscapeDataString(fileId)}"), ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            await ReadJsonAsync(response, "deleting a backup", ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
