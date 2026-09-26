using System.Text.Json;
using ReplayPad.Configuration;

namespace ReplayPad.Core;

/// <summary>Backup preferences, kept in backup.json next to the settings.</summary>
public sealed class BackupPrefs
{
    public bool AutoBackup { get; set; }
    public int EveryDays { get; set; } = 1;
    /// <summary>Drive backups kept per PC; older ones are deleted after a new one lands.</summary>
    public int KeepCount { get; set; } = 5;
    public bool IncludeReplays { get; set; } = true;
    public DateTime? LastBackupUtc { get; set; }

    private static string PrefsPath => Path.Combine(AppPaths.DataDir, "backup.json");

    public static BackupPrefs Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
                return JsonSerializer.Deserialize<BackupPrefs>(File.ReadAllText(PrefsPath)) ?? new BackupPrefs();
        }
        catch (Exception ex)
        {
            Logger.Log("backup.json unreadable, using defaults: " + ex.Message);
        }
        return new BackupPrefs();
    }

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(PrefsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Log("Could not save backup.json: " + ex.Message);
        }
    }
}

public sealed record BackupProgress(string Stage, double Fraction);

/// <summary>
/// Backup / restore of the whole library, to the user's own Google Drive
/// or to a local file. Entry points must be called on the UI thread (they
/// read and update the soundboard store); heavy work runs in the background.
/// One operation at a time.
/// </summary>
public sealed class CloudBackup(AppController controller)
{
    public GoogleAuth Auth { get; } = new();
    public BackupPrefs Prefs { get; } = BackupPrefs.Load();
    public GoogleDriveClient Drive => _drive ??= new GoogleDriveClient(Auth);
    private GoogleDriveClient? _drive;

    public bool IsBusy { get; private set; }

    /// <summary>Raised on the UI thread when busy state or sign-in changes.</summary>
    public event Action? StateChanged;

    private static string TempDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayPad", "Temp");

    private static string TempZip(string tag) => Path.Combine(TempDir, $"{tag}-{Guid.NewGuid():N}.zip");

    public bool IsAutoBackupDue =>
        Prefs.AutoBackup && GoogleAuth.IsConfigured && Auth.IsSignedIn &&
        (Prefs.LastBackupUtc is not DateTime last || DateTime.UtcNow - last >= TimeSpan.FromDays(Math.Max(1, Prefs.EveryDays)));

    /// <summary>What a backup would contain right now (for the window's summary line).</summary>
    public LibraryBackup.Manifest Preview()
        => LibraryBackup.Snapshot(controller.Settings.ResolveOutputFolder(), controller.Soundboard,
                                  Prefs.IncludeReplays, settings: null);

    private async Task<T> RunExclusive<T>(Func<Task<T>> work)
    {
        if (IsBusy)
            throw new InvalidOperationException("Another backup or restore is already running.");
        IsBusy = true;
        StateChanged?.Invoke();
        try
        {
            return await work();
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    public async Task SignInAsync(CancellationToken ct)
    {
        await Auth.SignInAsync(ct);
        try { Auth.SetEmail(await Drive.GetEmailAsync(ct)); }
        catch (Exception ex) { Logger.Log("Could not read the Google account e-mail: " + ex.Message); }
        StateChanged?.Invoke();
    }

    public async Task SignOutAsync()
    {
        await Auth.SignOutAsync();
        _drive = null;
        StateChanged?.Invoke();
    }

    /// <summary>Packs the library and uploads it to Drive; prunes this PC's old backups.</summary>
    public Task<GoogleDriveClient.BackupFile> BackUpToDriveAsync(IProgress<BackupProgress>? progress, CancellationToken ct)
        => RunExclusive(async () =>
        {
            string root = controller.Settings.ResolveOutputFolder();
            var manifest = LibraryBackup.Snapshot(root, controller.Soundboard, Prefs.IncludeReplays, controller.Settings);
            string zip = TempZip("backup");
            try
            {
                await Task.Run(() => LibraryBackup.WriteZip(root, manifest, zip,
                    Stage(progress, "Packing"), ct), ct);
                var uploaded = await Drive.UploadAsync(zip, manifest, Stage(progress, "Uploading"), ct);

                Prefs.LastBackupUtc = DateTime.UtcNow;
                Prefs.Save();
                Logger.Log($"Backed up to Google Drive: {uploaded.Name} ({uploaded.Size / 1048576.0:0.#} MB)");

                try { await PruneAsync(ct); }
                catch (Exception ex) { Logger.Log("Pruning old backups failed: " + ex.Message); }
                return uploaded;
            }
            finally
            {
                TryDelete(zip);
            }
        });

    /// <summary>Keeps the newest KeepCount backups of this PC; other PCs' backups are never touched.</summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        var mine = (await Drive.ListBackupsAsync(ct))
            .Where(b => string.Equals(b.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.CreatedUtc)
            .Skip(Math.Max(1, Prefs.KeepCount));
        foreach (var old in mine)
        {
            await Drive.DeleteAsync(old.Id, ct);
            Logger.Log("Deleted old backup " + old.Name);
        }
    }

    public Task<LibraryBackup.RestoreResult> RestoreFromDriveAsync(GoogleDriveClient.BackupFile backup, bool restoreSettings,
                                                                   IProgress<BackupProgress>? progress, CancellationToken ct)
        => RunExclusive(async () =>
        {
            string zip = TempZip("restore");
            try
            {
                await Drive.DownloadAsync(backup, zip, Stage(progress, "Downloading"), ct);
                return await RestoreCoreAsync(zip, restoreSettings, progress, ct);
            }
            finally
            {
                TryDelete(zip);
            }
        });

    public Task<LibraryBackup.RestoreResult> RestoreFromFileAsync(string zipPath, bool restoreSettings,
                                                                  IProgress<BackupProgress>? progress, CancellationToken ct)
        => RunExclusive(() => RestoreCoreAsync(zipPath, restoreSettings, progress, ct));

    public Task<LibraryBackup.Manifest> SaveToFileAsync(string zipPath, IProgress<BackupProgress>? progress, CancellationToken ct)
        => RunExclusive(async () =>
        {
            string root = controller.Settings.ResolveOutputFolder();
            var manifest = LibraryBackup.Snapshot(root, controller.Soundboard, Prefs.IncludeReplays, controller.Settings);
            // Write beside the target first, so a cancelled save never leaves a broken file.
            string temp = zipPath + ".partial";
            try
            {
                await Task.Run(() => LibraryBackup.WriteZip(root, manifest, temp, Stage(progress, "Packing"), ct), ct);
                File.Move(temp, zipPath, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
            return manifest;
        });

    private async Task<LibraryBackup.RestoreResult> RestoreCoreAsync(string zip, bool restoreSettings,
                                                                     IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        controller.Voice.Stop(); // playing files are locked
        string root = controller.Settings.ResolveOutputFolder();
        var result = await Task.Run(() => LibraryBackup.Restore(zip, root, Stage(progress, "Restoring"), ct), ct);

        // Back on the UI thread: the store isn't thread-safe.
        LibraryBackup.ApplyMetadata(result, controller.Soundboard, root);
        if (restoreSettings && result.SettingsJson != null)
            ApplySettingsFromBackup(result.SettingsJson);
        controller.RegisterHotkey();
        Logger.Log($"Restored backup: {result.Added} added, {result.AlreadyThere} already present, " +
                   $"{result.Renamed} renamed, {result.Failed.Count} failed.");
        return result;
    }

    /// <summary>Takes the backup's settings but keeps this PC's folder, API key and missing devices.</summary>
    private void ApplySettingsFromBackup(string json)
    {
        try
        {
            var current = controller.Settings;
            var restored = JsonSerializer.Deserialize<AppSettings>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (restored == null)
                return;
            restored.OutputFolder = current.OutputFolder;
            restored.GroqApiKey = current.GroqApiKey;
            if (!string.IsNullOrWhiteSpace(restored.VoiceDevice) &&
                !VoicePlayer.ListRenderDevices().Any(d => d.Contains(restored.VoiceDevice, StringComparison.OrdinalIgnoreCase)))
                restored.VoiceDevice = current.VoiceDevice;
            controller.ApplySettings(restored);
        }
        catch (Exception ex)
        {
            Logger.Log("Restoring settings failed (library was restored): " + ex);
        }
    }

    private static IProgress<double>? Stage(IProgress<BackupProgress>? progress, string stage)
        => progress == null ? null : new Progress<double>(f => progress.Report(new BackupProgress(stage, f)));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
