using System.IO.Compression;
using System.Text.Json;
using ReplayPad.Configuration;

namespace ReplayPad.Core;

/// <summary>
/// One-file snapshot of the whole library: every soundboard sound in its
/// category folder (empty categories included), the replays with their
/// transcripts, all pad metadata (labels, colors, volumes, pins, order,
/// hotkeys) and the app settings. Used for both Google Drive backups and
/// offline backup files.
///
/// Restoring never overwrites or deletes: files already present (same
/// name and size) are reused, clashes get " (1)" names, and metadata only
/// fills in what the current library doesn't already have.
/// </summary>
public static class LibraryBackup
{
    public const int FormatVersion = 1;
    private const string ManifestName = "backup.json";
    private const string SettingsName = "settings.json";
    private const string FilesPrefix = "library/";
    private const string BoardFolder = "Soundboard";

    public sealed class FileEntry
    {
        /// <summary>Path relative to the library root, '/'-separated.</summary>
        public string Rel { get; set; } = "";
        public long Size { get; set; }
        public string? Label { get; set; }
        public string? Color { get; set; }
        public int Volume { get; set; } = 100;
        public bool Pinned { get; set; }
        public int? Slot { get; set; }
        public string? Hotkey { get; set; }
    }

    public sealed class Manifest
    {
        public int Version { get; set; } = FormatVersion;
        public DateTime CreatedUtc { get; set; }
        public string Machine { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public List<FileEntry> Files { get; set; } = [];
        /// <summary>Every category folder (relative), so empty ones survive.</summary>
        public List<string> Folders { get; set; } = [];
        /// <summary>Manual pad order (relative paths).</summary>
        public List<string> Order { get; set; } = [];
        public int SoundCount { get; set; }
        public int ReplayCount { get; set; }
        public int CategoryCount { get; set; }
        public long TotalBytes { get; set; }
        public bool HasSettings { get; set; }

        /// <summary>Settings carried alongside (not part of backup.json itself).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SettingsJson { get; set; }
    }

    public sealed class RestoreResult
    {
        public Manifest Manifest { get; init; } = new();
        /// <summary>Relative path in the backup → where it lives now.</summary>
        public Dictionary<string, string> Landed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Added { get; set; }
        public int AlreadyThere { get; set; }
        public int Renamed { get; set; }
        public List<string> Failed { get; } = [];
        public string? SettingsJson { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static bool IsAudio(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav";

    private static string Rel(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Captures what to back up. Must run on the UI thread (it reads the
    /// soundboard store); the heavy zipping happens later in WriteZip.
    /// </summary>
    public static Manifest Snapshot(string root, SoundboardStore store, bool includeReplays, AppSettings? settings)
    {
        var manifest = new Manifest
        {
            CreatedUtc = DateTime.UtcNow,
            Machine = Environment.MachineName,
            AppVersion = UpdateChecker.CurrentVersion.ToString()
        };

        void AddAudio(FileInfo file)
        {
            manifest.Files.Add(new FileEntry
            {
                Rel = Rel(root, file.FullName),
                Size = file.Length,
                Label = store.GetLabel(file.FullName),
                Color = store.GetColor(file.FullName),
                Volume = store.GetVolume(file.FullName),
                Pinned = store.IsPinned(file.FullName),
                Slot = store.SlotOf(file.FullName),
                Hotkey = store.GetHotkey(file.FullName)
            });
            manifest.TotalBytes += file.Length;

            // Its transcript rides along.
            var txt = new FileInfo(Path.ChangeExtension(file.FullName, ".txt"));
            if (txt.Exists)
            {
                manifest.Files.Add(new FileEntry { Rel = Rel(root, txt.FullName), Size = txt.Length });
                manifest.TotalBytes += txt.Length;
            }
        }

        if (!Directory.Exists(root))
            return manifest;

        if (includeReplays)
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles().Where(f => IsAudio(f.Name)))
            {
                AddAudio(file);
                manifest.ReplayCount++;
            }
        }

        string board = Path.Combine(root, BoardFolder);
        if (Directory.Exists(board))
        {
            manifest.Folders.Add(BoardFolder);
            foreach (var dir in new DirectoryInfo(board).EnumerateDirectories("*", SearchOption.AllDirectories))
            {
                manifest.Folders.Add(Rel(root, dir.FullName));
                manifest.CategoryCount++;
            }

            var sounds = new DirectoryInfo(board)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => IsAudio(f.Name))
                .ToList();
            foreach (var file in sounds)
            {
                AddAudio(file);
                manifest.SoundCount++;
            }
            manifest.Order = sounds
                .Where(f => store.OrderIndexOf(f.FullName) != int.MaxValue)
                .OrderBy(f => store.OrderIndexOf(f.FullName))
                .Select(f => Rel(root, f.FullName))
                .ToList();
        }

        if (settings != null)
        {
            // Machine-specific or secret values stay out of the backup.
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            copy.OutputFolder = "";
            copy.GroqApiKey = "";
            manifest.SettingsJson = JsonSerializer.Serialize(copy, JsonOptions);
            manifest.HasSettings = true;
        }
        return manifest;
    }

    /// <summary>Writes the snapshot as a zip (background thread). Files deleted meanwhile are skipped.</summary>
    public static void WriteZip(string root, Manifest manifest, string zipPath,
                                IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        long done = 0;
        long total = Math.Max(1, manifest.TotalBytes);
        var buffer = new byte[1 << 20];
        var written = new List<FileEntry>();

        try
        {
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (string folder in manifest.Folders)
                    zip.CreateEntry(FilesPrefix + folder.TrimEnd('/') + "/");

                foreach (var entry in manifest.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    string source = Path.Combine(root, entry.Rel.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        // MP3 is already compressed; squeezing it again only burns CPU.
                        var level = source.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                            ? CompressionLevel.NoCompression
                            : CompressionLevel.Fastest;
                        using var output = zip.CreateEntry(FilesPrefix + entry.Rel, level).Open();
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            done += read;
                            progress?.Report(Math.Min(1, (double)done / total));
                        }
                        written.Add(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Logger.Log($"Backup skipped {entry.Rel}: {ex.Message}");
                    }
                }

                if (manifest.SettingsJson != null)
                    WriteText(zip, SettingsName, manifest.SettingsJson);

                // The manifest lists only what actually made it into the zip.
                manifest.Files = written;
                WriteText(zip, ManifestName, JsonSerializer.Serialize(manifest, JsonOptions));
            }
        }
        catch
        {
            try { File.Delete(zipPath); } catch { }
            throw;
        }
        progress?.Report(1);
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
        writer.Write(text);
    }

    public static Manifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return ReadManifest(zip);
    }

    private static Manifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName)
            ?? throw new InvalidDataException("This file is not a ReplayPad backup (backup.json is missing).");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(stream)
            ?? throw new InvalidDataException("The backup's manifest is unreadable.");
        if (manifest.Version > FormatVersion)
            throw new InvalidDataException("This backup was made by a newer ReplayPad — update the app to restore it.");
        return manifest;
    }

    /// <summary>
    /// Extracts a backup into the library (background thread). Pad
    /// metadata is applied afterwards with <see cref="ApplyMetadata"/> on
    /// the UI thread.
    /// </summary>
    public static RestoreResult Restore(string zipPath, string root,
                                        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = ReadManifest(zip);
        var result = new RestoreResult { Manifest = manifest };
        string rootFull = Path.GetFullPath(root);

        string? Target(string rel)
        {
            string full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            // No absolute paths or ".." escapes out of the library.
            return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }

        Directory.CreateDirectory(rootFull);
        foreach (string folder in manifest.Folders)
            if (Target(folder) is string dir)
                Directory.CreateDirectory(dir);

        if (zip.GetEntry(SettingsName) is ZipArchiveEntry settingsEntry)
        {
            using var reader = new StreamReader(settingsEntry.Open());
            result.SettingsJson = reader.ReadToEnd();
        }

        long total = Math.Max(1, manifest.Files.Sum(f => f.Size));
        long done = 0;

        // Audio first, so a transcript can follow its audio if that got renamed.
        var ordered = manifest.Files.OrderBy(f => IsAudio(f.Rel) ? 0 : 1).ToList();
        foreach (var file in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = zip.GetEntry(FilesPrefix + file.Rel);
                string? target = Target(file.Rel);
                if (entry == null || target == null)
                {
                    result.Failed.Add(file.Rel);
                    continue;
                }

                if (!IsAudio(file.Rel))
                {
                    // Transcript: sit next to wherever its audio landed.
                    string audioRel = result.Landed.Keys.FirstOrDefault(k =>
                        string.Equals(Path.ChangeExtension(k, ".txt"), file.Rel, StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (result.Landed.TryGetValue(audioRel, out string? audioPath))
                        target = Path.ChangeExtension(audioPath, ".txt");
                    if (!File.Exists(target))
                        ExtractTo(entry, target, ct);
                    result.Landed[file.Rel] = target;
                }
                else if (File.Exists(target) && new FileInfo(target).Length == entry.Length)
                {
                    result.Landed[file.Rel] = target; // already here — reuse it
                    result.AlreadyThere++;
                }
                else
                {
                    bool clash = File.Exists(target);
                    target = UniquePath(target);
                    ExtractTo(entry, target, ct);
                    result.Landed[file.Rel] = target;
                    if (clash) result.Renamed++; else result.Added++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"Restore of {file.Rel} failed: {ex.Message}");
                result.Failed.Add(file.Rel);
            }
            done += file.Size;
            progress?.Report(Math.Min(1, (double)done / total));
        }
        return result;
    }

    /// <summary>Extract via a temp file so a cancelled restore never leaves half a sound behind.</summary>
    private static void ExtractTo(ZipArchiveEntry entry, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + ".restoring";
        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write))
                input.CopyToAsync(output, ct).GetAwaiter().GetResult();
            File.Move(temp, target);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Applies labels, colors, volumes, pins, hotkeys, slots and order to
    /// the restored files (UI thread). Current values win: a restore fills
    /// in what's missing and never clobbers edits made since the backup.
    /// </summary>
    public static void ApplyMetadata(RestoreResult result, SoundboardStore store, string root)
    {
        using var batch = store.Batch();
        foreach (var file in result.Manifest.Files)
        {
            if (!IsAudio(file.Rel) || !result.Landed.TryGetValue(file.Rel, out string? path))
                continue;
            if (file.Label != null && store.GetLabel(path) == null)
                store.SetLabel(path, file.Label);
            if (file.Color != null && store.GetColor(path) == null)
                store.SetColor(path, file.Color);
            if (file.Volume != 100 && store.GetVolume(path) == 100)
                store.SetVolume(path, file.Volume);
            if (file.Pinned && !store.IsPinned(path))
                store.SetPinned(path, true);
            if (file.Hotkey != null && store.GetHotkey(path) == null)
                store.SetHotkey(path, file.Hotkey);
            if (file.Slot is int slot && store.PathOfSlot(slot) == null && store.SlotOf(path) == null)
                store.AssignSlot(slot, path);
        }

        // Keep the current manual order and append restored pads in the backup's order.
        string board = Path.Combine(root, BoardFolder);
        if (Directory.Exists(board) && result.Manifest.Order.Count > 0)
        {
            var ordered = new DirectoryInfo(board)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => f.FullName)
                .Where(p => IsAudio(p) && store.OrderIndexOf(p) != int.MaxValue)
                .OrderBy(store.OrderIndexOf)
                .ToList();
            foreach (string rel in result.Manifest.Order)
                if (result.Landed.TryGetValue(rel, out string? path) &&
                    !ordered.Contains(path, StringComparer.OrdinalIgnoreCase))
                    ordered.Add(path);
            store.SetOrder(ordered);
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        string folder = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
