using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ReplayPad.Configuration;
using ReplayPad.Core;
using CaptureMode = ReplayPad.Configuration.CaptureMode;

namespace ReplayPad.UI;

public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private readonly VoicePlayer _voicePlayer;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _volumeSaveTimer;
    private double _displayedLevel;
    private int _waveTicks;
    private bool _loadingUi;
    private bool _loadingDurations;
    private int _recentPage;
    private int _recentPageSize = 15; // 0 = All
    // Thread-safe: durations are filled in on a background thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, long Size, TimeSpan Duration)> _durationCache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record RecentItem(string Name, string Details, string FullPath);

    /// <summary>Pad view model; IsPlaying/Progress update live without rebuilding the grid.</summary>
    private sealed class SoundPad : System.ComponentModel.INotifyPropertyChanged
    {
        public required string Title { get; init; }
        public required string Sub { get; init; }
        public required string FullPath { get; init; }
        public Brush? Color { get; init; }
        public bool Pinned { get; init; }

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set { if (_isPlaying != value) { _isPlaying = value; Notify(nameof(IsPlaying)); } }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { if (Math.Abs(_progress - value) > 0.1) { _progress = value; Notify(nameof(Progress)); } }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    /// <summary>Selected category chip; null = All.</summary>
    private string? _selectedCategory;

    private const string SoundDragFormat = "ReplayPadSound";
    private Point _padDragStart;
    private SoundPad? _padDragCandidate;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        _voicePlayer = controller.Voice;
        InitializeComponent();

        _loadingUi = true;
        VoiceVolumeSlider.Value = controller.Settings.VoiceVolume;
        MirrorCheck.IsChecked = controller.Settings.VoiceAlsoSpeakers;
        _loadingUi = false;
        VoiceVolumeText.Text = $"{controller.Settings.VoiceVolume}%";
        _voicePlayer.SetVolume(controller.Settings.VoiceVolume / 100f);
        MirrorCheck.Checked += OnMirrorToggled;
        MirrorCheck.Unchecked += OnMirrorToggled;

        RecentPageSizeBox.SelectedIndex = 0; // "15"; also triggers the first list build
        UpdateStaticTexts();
        RefreshRecentList();
        RefreshSoundboard(); // soundboard is the home tab
        UpdateTabVisuals();

        _controller.ReplaySaved += (path, duration) => Dispatcher.BeginInvoke(() =>
        {
            ShowSaveStatus($"Saved {Path.GetFileName(path)} ({AppController.Fmt(duration)})", ok: true);
            RefreshRecentList();
        });
        _controller.SaveFailed += message => Dispatcher.BeginInvoke(() =>
            ShowSaveStatus("Save failed: " + message, ok: false));
        _controller.SoundboardError += message => Dispatcher.BeginInvoke(() =>
            ShowSaveStatus(message, ok: false));
        _controller.SaveBlocked += (blockedFolder, rescuedPath) => Dispatcher.BeginInvoke(() =>
            OnSaveBlocked(blockedFolder, rescuedPath));
        _voicePlayer.PlaybackEnded += () => Dispatcher.BeginInvoke(() =>
        {
            MicPlayBtn.Content = "Play to mic";
            RefreshSoundboard();
        });

        // Persisting the volume on every slider tick would hammer the disk;
        // save it shortly after the user stops moving it.
        _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            try { _controller.Settings.Save(); } catch (Exception ex) { Logger.Log("Volume save failed: " + ex.Message); }
        };

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UpdateLiveState();
        _uiTimer.Start();

        // Catch files that appeared while the window wasn't in focus
        // (standalone editor saves, manual folder changes, …).
        Activated += (_, _) => RefreshRecentList();
    }

    // ---------- live status ----------

    private void UpdateLiveState()
    {
        bool running = _controller.IsCapturing;

        StatusDot.Fill = (Brush)FindResource(running ? "AccentBrush" : "DimBrush");
        StatusText.Text = running ? "Recording" : "Stopped";
        PauseBtn.Content = running ? "■ Stop" : "▶ Start";
        RailDot.Fill = StatusDot.Fill;
        RailDot.ToolTip = running
            ? $"Recording — {AppController.Fmt(_controller.BufferedDuration)} buffered"
            : "Stopped — press Start in the Replays view";

        double peak = running ? _controller.CurrentPeak * 100 : 0;
        _displayedLevel = Math.Max(peak, _displayedLevel * 0.85);
        LevelBar.Value = Math.Min(100, _displayedLevel);

        var buffered = _controller.BufferedDuration;
        var capacity = _controller.BufferCapacity;
        BufferBar.Maximum = Math.Max(1, capacity.TotalSeconds);
        BufferBar.Value = Math.Min(buffered.TotalSeconds, capacity.TotalSeconds);
        BufferedText.Text = $"{AppController.Fmt(buffered)} / {AppController.Fmt(capacity)}";

        // The waveform only needs a few redraws per second.
        if (++_waveTicks >= 3 && IsVisible)
        {
            _waveTicks = 0;
            WaveView.Update(_controller.WaveformSnapshot);
        }

        // Live playing state + progress on the pads, without grid rebuilds.
        if (SoundboardView.Visibility == Visibility.Visible &&
            PadsGrid.ItemsSource is IEnumerable<SoundPad> pads)
        {
            foreach (var pad in pads)
            {
                bool playing = _voicePlayer.IsPlayingPath(pad.FullPath);
                pad.IsPlaying = playing;
                pad.Progress = playing ? (_voicePlayer.ProgressOf(pad.FullPath) ?? 0) * 100 : 0;
            }
        }

        UpdateTransportBar();
    }

    // ---------- now-playing transport ----------

    private bool _seeking;
    private bool _suppressSeekEvent;
    private TimeSpan _transportDuration;

    private void UpdateTransportBar()
    {
        var transport = _voicePlayer.ActiveTransport();
        if (transport == null)
        {
            if (TransportBar.Visibility != Visibility.Collapsed)
                TransportBar.Visibility = Visibility.Collapsed;
            return;
        }

        TransportBar.Visibility = Visibility.Visible;
        _transportDuration = transport.Duration;
        TransportPlayBtn.Content = transport.IsPaused ? "▶" : "⏸";
        TransportDur.Text = AppController.Fmt(transport.Duration);

        if (!_seeking)
        {
            TransportPos.Text = AppController.Fmt(transport.Position);
            double fraction = transport.Duration.TotalSeconds > 0
                ? transport.Position.TotalSeconds / transport.Duration.TotalSeconds
                : 0;
            _suppressSeekEvent = true;
            TransportSeek.Value = fraction * 1000;
            _suppressSeekEvent = false;
        }
    }

    private void OnTransportPlayPause(object sender, RoutedEventArgs e)
    {
        bool nowPlaying = _voicePlayer.TogglePauseActive();
        TransportPlayBtn.Content = nowPlaying ? "⏸" : "▶";
    }

    private void OnTransportStop(object sender, RoutedEventArgs e)
    {
        _voicePlayer.Stop();
        MicPlayBtn.Content = "Play to mic";
        TransportBar.Visibility = Visibility.Collapsed;
        RefreshSoundboard();
    }

    // _seeking blocks the timer from moving the thumb while the user drags.
    private void OnSeekPressed(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void OnSeekReleased(object sender, MouseButtonEventArgs e) => _seeking = false;

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Ignore only the timer's own position updates; every other change
        // (click, drag, keyboard) is a real user seek and scrubs live.
        if (_suppressSeekEvent)
            return;
        double fraction = TransportSeek.Value / 1000.0;
        if (_transportDuration.TotalSeconds > 0)
            TransportPos.Text = AppController.Fmt(_transportDuration * fraction);
        _voicePlayer.SeekActive(fraction);
    }

    private void UpdateStaticTexts()
    {
        var s = _controller.Settings;
        string source = s.Mode switch
        {
            CaptureMode.Desktop => "Desktop audio",
            CaptureMode.Microphone => "Microphone",
            _ => "Desktop + microphone"
        };
        if (s.Mode != CaptureMode.Microphone && !string.IsNullOrWhiteSpace(s.TargetApp))
            source = s.TargetAppExclude ? $"Everything except {s.TargetApp}" : $"{s.TargetApp} only";
        StatusSub.Text = $"{source} — keeping the last {s.BufferMinutes} min";
        HotkeyHint.Text = $"or press {s.Hotkey} anywhere";
        ClipBtn.Content = $"Save last {s.ClipSeconds} s  ({s.ClipHotkey})";
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_controller.IsCapturing)
                _controller.StopCapture();
            else
                _controller.StartCapture();
        }
        catch (Exception ex)
        {
            Logger.Log("Pause/resume failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_controller) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Saved)
        {
            UpdateStaticTexts();
            RefreshRecentList();
            _loadingUi = true;
            MirrorCheck.IsChecked = _controller.Settings.VoiceAlsoSpeakers;
            _loadingUi = false;
        }
    }

    // ---------- saving ----------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ShowSaveStatus("Encoding…", ok: true);
        _controller.SaveReplay();
    }

    private void OnClipClick(object sender, RoutedEventArgs e)
    {
        ShowSaveStatus("Encoding clip…", ok: true);
        _controller.SaveClip();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _controller.ClearBuffer();
        WaveView.Update([]);
        ShowSaveStatus("Buffer cleared — recording continues from now", ok: true);
    }

    private bool _blockedDialogOpen;

    /// <summary>The output folder is blocked; the save was rescued — guide the fix.</summary>
    private void OnSaveBlocked(string blockedFolder, string rescuedPath)
    {
        ShowSaveStatus($"Windows blocked \"{blockedFolder}\" — clip rescued to {Path.GetFileName(rescuedPath)}.", ok: false);
        if (_blockedDialogOpen)
            return;
        _blockedDialogOpen = true;
        try
        {
            var dialog = new BlockedSaveDialog(blockedFolder);
            if (IsVisible)
                dialog.Owner = this;
            dialog.ShowDialog();

            if (dialog.SwitchToFolder is string newFolder)
            {
                var current = _controller.Settings;
                var updated = new AppSettings
                {
                    CaptureMode = current.CaptureMode,
                    TargetApp = current.TargetApp,
                    TargetAppExclude = current.TargetAppExclude,
                    DesktopGain = current.DesktopGain,
                    MicrophoneGain = current.MicrophoneGain,
                    BufferMinutes = current.BufferMinutes,
                    Bitrate = current.Bitrate,
                    OutputFolder = newFolder,
                    Hotkey = current.Hotkey,
                    ClipHotkey = current.ClipHotkey,
                    ClipSeconds = current.ClipSeconds,
                    LauncherHotkey = current.LauncherHotkey,
                    StopHotkey = current.StopHotkey,
                    GroqApiKey = current.GroqApiKey,
                    VoiceDevice = current.VoiceDevice,
                    VoiceAlsoSpeakers = current.VoiceAlsoSpeakers,
                    VoiceVolume = current.VoiceVolume,
                    SoundboardOverlap = current.SoundboardOverlap,
                    ShowNotifications = current.ShowNotifications,
                    FileNamePrefix = current.FileNamePrefix,
                    KeepAliveSilence = current.KeepAliveSilence,
                    FFmpegPath = current.FFmpegPath
                };
                _controller.ApplySettings(updated);

                // Bring the rescued clip into the new library.
                try
                {
                    Directory.CreateDirectory(newFolder);
                    string target = Path.Combine(newFolder, Path.GetFileName(rescuedPath));
                    if (File.Exists(rescuedPath) && !File.Exists(target))
                        File.Move(rescuedPath, target);
                }
                catch (Exception ex)
                {
                    Logger.Log("Could not move rescued clip: " + ex.Message);
                }

                UpdateStaticTexts();
                RefreshRecentList();
                RefreshSoundboard();
                ShowSaveStatus($"Library switched to {newFolder}.", ok: true);
            }
        }
        finally
        {
            _blockedDialogOpen = false;
        }
    }

    private void ShowSaveStatus(string text, bool ok)
    {
        SaveStatus.Text = text;
        SaveStatus.Foreground = (Brush)FindResource(ok ? "GreenBrush" : "WarnBrush");
        SaveStatus.Visibility = Visibility.Visible;
    }

    // ---------- recent replays ----------

    private void RefreshRecentList()
    {
        List<FileInfo> all = [];
        try
        {
            string folder = _controller.Settings.ResolveOutputFolder();
            if (Directory.Exists(folder))
                all = new DirectoryInfo(folder)
                    .EnumerateFiles()
                    .Where(f => f.Extension is ".mp3" or ".wav")
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
        }
        catch (Exception ex)
        {
            Logger.Log("Could not list recent replays: " + ex.Message);
        }

        var store = _controller.Soundboard;
        string query = RecentSearchBox.Text.Trim();

        // Search filter over the whole library.
        var filtered = all
            .Select(f => (File: f, Label: store.GetLabel(f.FullName)))
            .Where(x => query.Length == 0 ||
                        (x.Label ?? x.File.Name).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        x.File.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Paging (page size 0 = All). Clamp the page in case the count shrank.
        int pageSize = _recentPageSize <= 0 ? Math.Max(filtered.Count, 1) : _recentPageSize;
        int pageCount = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
        _recentPage = Math.Clamp(_recentPage, 0, pageCount - 1);
        var page = filtered.Skip(_recentPage * pageSize).Take(pageSize).ToList();

        var missing = new List<FileInfo>();
        var items = page.Select(x =>
        {
            var f = x.File;
            int? slot = store.SlotOf(f.FullName);
            TimeSpan? duration = TryCachedDuration(f);
            if (duration == null)
                missing.Add(f);
            string details = $"{f.Length / 1024.0 / 1024.0:0.0} MB · {f.LastWriteTime:ddd HH:mm}";
            if (duration is TimeSpan d)
                details = $"{AppController.Fmt(d)} · " + details;
            if (x.Label != null)
                details = f.Name + " · " + details;
            if (slot != null)
                details += $"  ·  ⌨ Ctrl+Alt+{slot}";
            return new RecentItem(x.Label ?? f.Name, details, f.FullName);
        }).ToList();

        RecentList.ItemsSource = items;
        RecentTitle.Text = all.Count == 0 ? "Recent replays"
            : query.Length > 0 ? $"Recent replays — {filtered.Count} of {all.Count}"
            : $"Recent replays ({all.Count})";
        NoRecentText.Text = all.Count == 0
            ? "No replays yet — something worth keeping? Hit save."
            : "No replays match your search.";
        NoRecentText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Pager row: hidden when a single page holds everything.
        RecentPageText.Text = $"Page {_recentPage + 1} of {pageCount}";
        RecentPrevBtn.IsEnabled = _recentPage > 0;
        RecentNextBtn.IsEnabled = _recentPage < pageCount - 1;
        RecentPager.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Durations are read off the UI thread (opening files is slow with a
        // large library); refresh once they're cached so they pop in.
        if (missing.Count > 0 && !_loadingDurations)
        {
            _loadingDurations = true;
            var toLoad = missing.ToList();
            Task.Run(() =>
            {
                foreach (var f in toLoad)
                    try { GetDuration(f); } catch { }
            }).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                _loadingDurations = false;
                RefreshRecentList();
            }));
        }
    }

    private void OnRecentSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _recentPage = 0;
        RefreshRecentList();
    }

    private void OnRecentPageSizeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecentPageSizeBox.SelectedItem is not System.Windows.Controls.ComboBoxItem sel)
            return;
        _recentPageSize = sel.Content as string == "All" ? 0 : int.Parse((string)sel.Content!);
        _recentPage = 0;
        RefreshRecentList();
    }

    private void OnRecentPrev(object sender, RoutedEventArgs e)
    {
        if (_recentPage > 0)
        {
            _recentPage--;
            RefreshRecentList();
            RecentList.ScrollIntoView(RecentList.Items.Count > 0 ? RecentList.Items[0] : null);
        }
    }

    private void OnRecentNext(object sender, RoutedEventArgs e)
    {
        _recentPage++;
        RefreshRecentList();
        RecentList.ScrollIntoView(RecentList.Items.Count > 0 ? RecentList.Items[0] : null);
    }

    private TimeSpan? TryCachedDuration(FileInfo f)
        => _durationCache.TryGetValue(f.FullName, out var c) && c.Stamp == f.LastWriteTime && c.Size == f.Length
            ? c.Duration
            : null;

    /// <summary>
    /// Audio duration via Media Foundation, cached by (path, size, mtime)
    /// so the frequent list refreshes don't reopen every file.
    /// </summary>
    private TimeSpan? GetDuration(FileInfo f)
    {
        if (_durationCache.TryGetValue(f.FullName, out var cached) &&
            cached.Stamp == f.LastWriteTime && cached.Size == f.Length)
            return cached.Duration;
        try
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(f.FullName);
            var duration = reader.TotalTime;
            _durationCache[f.FullName] = (f.LastWriteTime, f.Length, duration);
            return duration;
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not read duration of {f.Name}: {ex.Message}");
            return null;
        }
    }

    private void OnRecentDoubleClick(object sender, MouseButtonEventArgs e)
        => PlaySelectedToMic(toggle: false); // double-click retriggers, soundboard-style

    /// <summary>Selects the item under the cursor before the context menu opens.</summary>
    private void OnRecentPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not System.Windows.Controls.ListBoxItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is System.Windows.Controls.ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnRecentMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (RecentList.SelectedItem == null)
            e.Handled = true; // right-clicked empty space — nothing to act on
    }

    private void OnPlayLocalClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item && File.Exists(item.FullPath))
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
    }

    private void OpenTranscription(string path)
    {
        string apiKey = _controller.Settings.GroqApiKey.Trim();
        if (apiKey.Length == 0)
        {
            ShowSaveStatus(
                "Transcription needs a free Groq API key — create one at console.groq.com and paste it in ⚙ Settings → Transcription.",
                ok: false);
            return;
        }
        new TranscriptWindow(path, apiKey) { Owner = this }.Show();
    }

    private void OnTranscribeReplayClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item && File.Exists(item.FullPath))
            OpenTranscription(item.FullPath);
    }

    private void OnPadTranscribeClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is SoundPad pad && File.Exists(pad.FullPath))
            OpenTranscription(pad.FullPath);
    }

    private void OnShowInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first, then press Edit.", ok: false);
            return;
        }
        var editor = new EditorWindow(item.FullPath, _controller.Settings) { Owner = this };
        // Edited copies must show up in this list right away so they can be
        // played to the mic without reopening the app.
        editor.FileSaved += () => Dispatcher.BeginInvoke(RefreshRecentList);
        editor.Show();
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }
        ShowSoundProperties(item.FullPath);
    }

    /// <summary>Label / rename / category / hotkey / volume dialog, shared by replays and pads.</summary>
    private void ShowSoundProperties(string path)
    {
        var store = _controller.Soundboard;
        string extension = Path.GetExtension(path);
        string lib = _controller.SoundLibraryDir;

        // Categories only apply to sounds inside the soundboard library.
        string parentDir = Path.GetDirectoryName(path)!;
        bool isLibrarySound = Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(lib) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string? currentCategory = isLibrarySound &&
            !string.Equals(Path.GetFullPath(parentDir), Path.GetFullPath(lib), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(parentDir)
                : null;

        var dialog = new RenameDialog(
            store.GetLabel(path) ?? "",
            Path.GetFileNameWithoutExtension(path),
            extension,
            store.SlotOf(path),
            store.PathOfSlot,
            store.GetVolume(path),
            isLibrarySound ? GetCategories() : null,
            currentCategory,
            store.GetColor(path),
            store.GetHotkey(path))
        { Owner = this };
        dialog.ShowDialog();
        if (!dialog.Saved)
            return;

        try
        {
            if (!string.Equals(dialog.FileNameResult, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
            {
                string newPath = Path.Combine(Path.GetDirectoryName(path)!, dialog.FileNameResult + extension);
                if (File.Exists(newPath))
                {
                    ShowSaveStatus($"A file named \"{dialog.FileNameResult}{extension}\" already exists.", ok: false);
                    return;
                }
                File.Move(path, newPath);
                store.RenameFile(path, newPath);
                path = newPath;
            }

            if (isLibrarySound &&
                !string.Equals(dialog.CategoryResult, currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                string targetDir = dialog.CategoryResult == null ? lib : Path.Combine(lib, dialog.CategoryResult);
                Directory.CreateDirectory(targetDir);
                string movedPath = ReplaySaver.UniquePath(targetDir, Path.GetFileNameWithoutExtension(path), extension);
                File.Move(path, movedPath);
                store.RenameFile(path, movedPath);
                path = movedPath;
            }

            store.SetLabel(path, dialog.LabelResult);
            store.AssignSlot(dialog.SlotResult, path);
            store.SetVolume(path, dialog.VolumeResult);
            store.SetColor(path, dialog.ColorResult);
            store.SetHotkey(path, dialog.CustomHotkeyResult);
            string warning = _controller.RegisterHotkey();
            RefreshRecentList();
            RefreshSoundboard();
            ShowSaveStatus(warning.Length > 0 ? "Saved, but: " + warning : "Saved.", ok: warning.Length == 0);
        }
        catch (Exception ex)
        {
            Logger.Log("Rename failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnDeleteReplayClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }

        try
        {
            // If this exact file is streaming into a call, release it first.
            if (_voicePlayer.IsPlaying)
            {
                _voicePlayer.Stop();
                MicPlayBtn.Content = "Play to mic";
            }
            RecycleBin.Delete(item.FullPath);
            _controller.Soundboard.RemoveFile(item.FullPath);
            _controller.RegisterHotkey();
            RefreshRecentList();
            ShowSaveStatus($"{item.Name} moved to the Recycle Bin.", ok: true);
        }
        catch (Exception ex)
        {
            Logger.Log("Delete replay failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        string folder = _controller.Settings.ResolveOutputFolder();
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowSaveStatus("Could not open folder: " + ex.Message, ok: false);
        }
    }

    // ---------- soundboard panel ----------

    private void OnReplaysTabClick(object sender, RoutedEventArgs e)
    {
        ReplayView.Visibility = Visibility.Visible;
        SoundboardView.Visibility = Visibility.Collapsed;
        UpdateTabVisuals();
        RefreshRecentList();
    }

    private void OnSoundboardTabClick(object sender, RoutedEventArgs e)
    {
        ReplayView.Visibility = Visibility.Collapsed;
        SoundboardView.Visibility = Visibility.Visible;
        UpdateTabVisuals();
        RefreshSoundboard();
    }

    private void UpdateTabVisuals()
    {
        bool soundboard = SoundboardView.Visibility == Visibility.Visible;
        ReplaysTabBtn.Opacity = soundboard ? 0.45 : 1.0;
        SoundboardTabBtn.Opacity = soundboard ? 1.0 : 0.45;
    }

    /// <summary>Category subfolder names inside the soundboard library.</summary>
    private List<string> GetCategories()
    {
        try
        {
            string lib = _controller.SoundLibraryDir;
            if (Directory.Exists(lib))
                return Directory.GetDirectories(lib)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch (Exception ex)
        {
            Logger.Log("Could not list categories: " + ex.Message);
        }
        return [];
    }

    private void RebuildCategoryChips(List<string> categories)
    {
        if (_selectedCategory != null && !categories.Contains(_selectedCategory, StringComparer.OrdinalIgnoreCase))
            _selectedCategory = null;

        CategoryChips.Children.Clear();
        AddCategoryChip("All", null, categories.Count > 0);
        foreach (string category in categories)
            AddCategoryChip(category, category, true);

        var newBtn = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("Btn"),
            Content = "＋ Category",
            FontSize = 11,
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Opacity = 0.7,
            ToolTip = "Create a new category (a subfolder of the soundboard library)"
        };
        newBtn.Click += OnNewCategoryClick;
        CategoryChips.Children.Add(newBtn);
    }

    private void AddCategoryChip(string text, string? value, bool visible)
    {
        if (!visible && value == null)
            return; // hide "All" when there are no categories yet
        bool selected = string.Equals(_selectedCategory, value, StringComparison.OrdinalIgnoreCase) ||
                        (_selectedCategory == null && value == null);
        var chip = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("Btn"),
            Content = text,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Opacity = selected ? 1.0 : 0.5,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            AllowDrop = true,
            ToolTip = value == null
                ? "Show all sounds — drop a pad here to move it out of its category"
                : $"Show only \"{text}\" — drop a pad here to move it in (hold Ctrl to copy), or drop files to import"
        };
        chip.Click += (_, _) =>
        {
            _selectedCategory = value;
            RefreshSoundboard();
        };
        chip.DragOver += (_, e) => OnChipDragOver(e);
        chip.Drop += (_, e) => OnChipDrop(value, e);
        CategoryChips.Children.Add(chip);
    }

    private static void OnChipDragOver(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SoundDragFormat))
            e.Effects = (Keyboard.Modifiers & ModifierKeys.Control) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnChipDrop(string? category, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(SoundDragFormat) is string soundPath)
        {
            bool copy = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            MoveSoundToCategory(soundPath, category, copy);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ImportSoundsTo(files, category);
        }
    }

    // ---- pad dragging (click still works; drag starts after a movement threshold) ----

    private void OnPadMouseDown(object sender, MouseButtonEventArgs e)
    {
        _padDragCandidate = (sender as FrameworkElement)?.Tag as SoundPad;
        _padDragStart = e.GetPosition(this);
    }

    private void OnPadMouseMove(object sender, MouseEventArgs e)
    {
        if (_padDragCandidate == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        Point position = e.GetPosition(this);
        if (Math.Abs(position.X - _padDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _padDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var pad = _padDragCandidate;
        _padDragCandidate = null;
        var data = new DataObject(SoundDragFormat, pad.FullPath);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    /// <summary>Moves (or copies) a library sound into a category; null = main board.</summary>
    private void MoveSoundToCategory(string path, string? category, bool copy)
    {
        try
        {
            if (!File.Exists(path))
                return;
            string lib = _controller.SoundLibraryDir;
            string targetDir = category == null ? lib : Path.Combine(lib, category);
            Directory.CreateDirectory(targetDir);

            bool sameFolder = string.Equals(
                Path.GetFullPath(Path.GetDirectoryName(path)!),
                Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase);
            if (!copy && sameFolder)
                return;

            var store = _controller.Soundboard;
            string newPath = ReplaySaver.UniquePath(targetDir, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
            if (copy)
            {
                File.Copy(path, newPath);
                if (store.GetLabel(path) is string label)
                    store.SetLabel(newPath, label);
                int volume = store.GetVolume(path);
                if (volume != 100)
                    store.SetVolume(newPath, volume);
            }
            else
            {
                _voicePlayer.StopPath(path);
                File.Move(path, newPath);
                store.RenameFile(path, newPath);
            }
            RefreshSoundboard();
            ShowSaveStatus(
                $"{Path.GetFileName(newPath)} {(copy ? "copied" : "moved")} to {category ?? "the main board"}.",
                ok: true);
        }
        catch (Exception ex)
        {
            Logger.Log("Move/copy sound failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnPadMoveClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is not SoundPad pad || !File.Exists(pad.FullPath))
            return;
        string lib = _controller.SoundLibraryDir;
        string parent = Path.GetDirectoryName(pad.FullPath)!;
        string? current = string.Equals(Path.GetFullPath(parent), Path.GetFullPath(lib), StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetFileName(parent);
        var picker = new CategoryPickerDialog(GetCategories(), current) { Owner = this };
        picker.ShowDialog();
        if (picker.Confirmed)
            MoveSoundToCategory(pad.FullPath, picker.Category, picker.IsCopy);
    }

    private void OnNewCategoryClick(object sender, RoutedEventArgs e)
    {
        var prompt = new InputDialog("New category", "Category name (e.g. memes, music)") { Owner = this };
        prompt.ShowDialog();
        if (prompt.Result is not string name || name.Length == 0)
            return;
        try
        {
            Directory.CreateDirectory(Path.Combine(_controller.SoundLibraryDir, name));
            _selectedCategory = name;
            RefreshSoundboard();
        }
        catch (Exception ex)
        {
            Logger.Log("Create category failed: " + ex);
            ShowSaveStatus("Could not create category: " + ex.Message, ok: false);
        }
    }

    /// <summary>Rebuilds the category chips and pad grid from the library folder.</summary>
    private void RefreshSoundboard()
    {
        if (SoundboardView.Visibility != Visibility.Visible)
            return;

        var store = _controller.Soundboard;
        string query = PadSearchBox.Text.Trim();
        string lib = _controller.SoundLibraryDir;
        var categories = GetCategories();
        RebuildCategoryChips(categories);

        var pads = new List<SoundPad>();
        try
        {
            if (Directory.Exists(lib))
            {
                IEnumerable<FileInfo> files;
                if (_selectedCategory == null)
                {
                    files = new DirectoryInfo(lib).EnumerateFiles("*", SearchOption.AllDirectories);
                }
                else
                {
                    string dir = Path.Combine(lib, _selectedCategory);
                    files = Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles() : [];
                }

                pads = files
                    .Where(f => f.Extension is ".mp3" or ".wav")
                    .Select(f =>
                    {
                        string? label = store.GetLabel(f.FullName);
                        bool pinned = store.IsPinned(f.FullName);
                        string title = (pinned ? "📌 " : "") + (label ?? Path.GetFileNameWithoutExtension(f.Name));
                        int? slot = store.SlotOf(f.FullName);
                        TimeSpan? duration = GetDuration(f);
                        string sub = duration is TimeSpan d ? AppController.Fmt(d) : "";
                        if (slot != null)
                            sub += $"  ⌨{slot}";
                        if (store.GetHotkey(f.FullName) is string customHotkey)
                            sub += $"  ⌨{customHotkey}";
                        int volume = store.GetVolume(f.FullName);
                        if (volume != 100)
                            sub += $"  {volume}%";
                        // In the All view, show which category a sound lives in.
                        string parent = Path.GetFileName(f.DirectoryName ?? "");
                        if (_selectedCategory == null &&
                            !string.Equals(f.DirectoryName, lib, StringComparison.OrdinalIgnoreCase) &&
                            parent.Length > 0)
                            sub += $"  · {parent}";
                        return new SoundPad
                        {
                            Title = title,
                            Sub = sub.Trim(),
                            FullPath = f.FullName,
                            Color = ParsePadColor(store.GetColor(f.FullName)),
                            Pinned = pinned,
                            IsPlaying = _voicePlayer.IsPlayingPath(f.FullName)
                        };
                    })
                    .Where(p => query.Length == 0 ||
                                p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(p.FullPath).Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Pinned)
                    .ThenBy(p => store.OrderIndexOf(p.FullPath))
                    .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Could not list soundboard: " + ex.Message);
        }

        PadsGrid.ItemsSource = pads;
        NoPadsText.Visibility = pads.Count == 0 && query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush? ParsePadColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            var brush = (Brush?)new BrushConverter().ConvertFromString(hex);
            brush?.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private void OnPadSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RefreshSoundboard();

    private void OnStopAllClick(object sender, RoutedEventArgs e)
    {
        _voicePlayer.Stop();
        MicPlayBtn.Content = "Play to mic";
        RefreshSoundboard();
    }

    private void OnPadPinClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is not SoundPad pad)
            return;
        var store = _controller.Soundboard;
        store.SetPinned(pad.FullPath, !store.IsPinned(pad.FullPath));
        RefreshSoundboard();
    }

    private void OnPadClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SoundPad pad)
            return;
        if (_voicePlayer.IsPlayingPath(pad.FullPath))
            _voicePlayer.StopPath(pad.FullPath);
        else
            _controller.PlaySound(pad.FullPath);
        RefreshSoundboard();
    }

    private static SoundPad? PadFromMenuItem(object sender)
        => ((sender as System.Windows.Controls.MenuItem)?.Parent as System.Windows.Controls.ContextMenu)?
            .PlacementTarget is FrameworkElement fe ? fe.Tag as SoundPad : null;

    private void OnPadPlayLocalClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is SoundPad pad && File.Exists(pad.FullPath))
            Process.Start(new ProcessStartInfo(pad.FullPath) { UseShellExecute = true });
    }

    private void OnPadPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is SoundPad pad)
            ShowSoundProperties(pad.FullPath);
    }

    private void OnPadEditClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is not SoundPad pad || !File.Exists(pad.FullPath))
            return;
        var editor = new EditorWindow(pad.FullPath, _controller.Settings) { Owner = this };
        editor.FileSaved += () => Dispatcher.BeginInvoke(RefreshSoundboard);
        editor.Show();
    }

    private void OnPadExplorerClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is SoundPad pad && File.Exists(pad.FullPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{pad.FullPath}\"") { UseShellExecute = true });
    }

    private void OnPadDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PadFromMenuItem(sender) is not SoundPad pad)
            return;
        try
        {
            _voicePlayer.StopPath(pad.FullPath);
            RecycleBin.Delete(pad.FullPath);
            _controller.Soundboard.RemoveFile(pad.FullPath);
            _controller.RegisterHotkey();
            RefreshSoundboard();
        }
        catch (Exception ex)
        {
            Logger.Log("Delete sound failed: " + ex);
            ShowSaveStatus(ex.Message, ok: false);
        }
    }

    private void OnAddSoundsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add sounds to the soundboard",
            Filter = "Audio files (*.mp3;*.wav)|*.mp3;*.wav",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
            ImportSounds(dialog.FileNames);
    }

    private void OnSoundboardDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SoundDragFormat))
            e.Effects = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSoundboardDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(SoundDragFormat) is string soundPath)
            ReorderSound(soundPath, null); // dropped on empty space → move to the end
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            ImportSounds(files);
    }

    // ---- drag-to-reorder: drop a pad onto another pad to place it before it ----

    private void OnPadDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SoundDragFormat))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnPadDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(SoundDragFormat) is not string draggedPath ||
            (sender as FrameworkElement)?.Tag is not SoundPad target)
            return;
        e.Handled = true;
        if (!string.Equals(draggedPath, target.FullPath, StringComparison.OrdinalIgnoreCase))
            ReorderSound(draggedPath, target.FullPath);
    }

    /// <summary>
    /// Moves a sound before the target (null = to the end) and persists the
    /// full manual order. The order is global, so it holds across category
    /// views and search filters.
    /// </summary>
    private void ReorderSound(string draggedPath, string? targetPath)
    {
        try
        {
            var store = _controller.Soundboard;
            string lib = _controller.SoundLibraryDir;
            if (!Directory.Exists(lib))
                return;

            // Materialize the current effective order of the whole library.
            var all = new DirectoryInfo(lib)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => f.Extension is ".mp3" or ".wav")
                .Select(f => f.FullName)
                .OrderBy(store.OrderIndexOf)
                .ThenBy(p => store.GetLabel(p) ?? Path.GetFileNameWithoutExtension(p),
                        StringComparer.OrdinalIgnoreCase)
                .ToList();

            all.RemoveAll(p => string.Equals(p, draggedPath, StringComparison.OrdinalIgnoreCase));
            int insertAt = targetPath == null
                ? all.Count
                : all.FindIndex(p => string.Equals(p, targetPath, StringComparison.OrdinalIgnoreCase));
            if (insertAt < 0)
                insertAt = all.Count;
            all.Insert(insertAt, draggedPath);

            store.SetOrder(all);
            RefreshSoundboard();
        }
        catch (Exception ex)
        {
            Logger.Log("Reorder failed: " + ex);
        }
    }

    /// <summary>Copies audio files into the library — into the selected category, if one is active.</summary>
    private List<(string Source, string Target)> ImportSounds(IEnumerable<string> files)
        => ImportSoundsTo(files, _selectedCategory);

    /// <summary>Returns the paths of the files that landed in the library (source → target).</summary>
    private List<(string Source, string Target)> ImportSoundsTo(IEnumerable<string> files, string? category)
    {
        var landed = new List<(string, string)>();
        try
        {
            string dir = category == null
                ? _controller.SoundLibraryDir
                : Path.Combine(_controller.SoundLibraryDir, category);
            Directory.CreateDirectory(dir);
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".mp3" or ".wav") || !File.Exists(file))
                    continue;
                string target = ReplaySaver.UniquePath(dir, Path.GetFileNameWithoutExtension(file), ext);
                File.Copy(file, target);
                landed.Add((file, target));
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Import failed: " + ex);
            ShowSaveStatus("Import failed: " + ex.Message, ok: false);
        }
        RefreshSoundboard();
        if (landed.Count > 0)
            ShowSaveStatus($"Added {landed.Count} sound{(landed.Count == 1 ? "" : "s")} to the soundboard.", ok: true);
        return landed;
    }

    private void OnExportBoardClick(object sender, RoutedEventArgs e)
    {
        string lib = _controller.SoundLibraryDir;
        if (!Directory.Exists(lib))
        {
            ShowSaveStatus("The soundboard is empty — nothing to export.", ok: false);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export soundboard",
            Filter = "Soundboard package (*.zip)|*.zip",
            FileName = $"Soundboard-{DateTime.Now:yyyy-MM-dd}.zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            int count = BoardPackage.Export(lib, _controller.Soundboard, dialog.FileName);
            ShowSaveStatus($"Exported {count} sound{(count == 1 ? "" : "s")} to {Path.GetFileName(dialog.FileName)}.", ok: true);
        }
        catch (Exception ex)
        {
            Logger.Log("Board export failed: " + ex);
            ShowSaveStatus("Export failed: " + ex.Message, ok: false);
        }
    }

    private void OnImportBoardClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import soundboard package",
            Filter = "Soundboard package (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            int count = BoardPackage.Import(dialog.FileName, _controller.SoundLibraryDir, _controller.Soundboard);
            string warning = _controller.RegisterHotkey();
            RefreshSoundboard();
            ShowSaveStatus(
                $"Imported {count} sound{(count == 1 ? "" : "s")}." +
                (warning.Length > 0 ? " Note: " + warning : ""), ok: warning.Length == 0);
        }
        catch (Exception ex)
        {
            Logger.Log("Board import failed: " + ex);
            ShowSaveStatus("Import failed: " + ex.Message, ok: false);
        }
    }

    private void OnSendToSoundboardClick(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }

        var landed = ImportSounds([item.FullPath]);
        if (landed.Count == 0)
            return;

        // Carry the replay's label onto the new pad so it keeps its name.
        var store = _controller.Soundboard;
        string? label = store.GetLabel(item.FullPath);
        if (!string.IsNullOrWhiteSpace(label))
        {
            store.SetLabel(landed[0].Target, label);
            RefreshSoundboard();
        }
        ShowSaveStatus($"{item.Name} added to the soundboard.", ok: true);
    }

    // ---------- play to mic ----------

    private void OnPlayToMicClick(object sender, RoutedEventArgs e)
        => PlaySelectedToMic(toggle: true);

    /// <param name="toggle">
    /// True (button): a second press stops playback. False (double-click):
    /// always restart with the chosen replay, like a soundboard pad.
    /// </param>
    private void PlaySelectedToMic(bool toggle)
    {
        if (toggle && _voicePlayer.IsPlaying)
        {
            _voicePlayer.Stop();
            MicPlayBtn.Content = "Play to mic";
            return;
        }

        if (RecentList.SelectedItem is not RecentItem item || !File.Exists(item.FullPath))
        {
            ShowSaveStatus("Select a replay in the list first.", ok: false);
            return;
        }

        if (_controller.PlaySound(item.FullPath))
        {
            MicPlayBtn.Content = "■ Stop mic";
            ShowSaveStatus($"Playing {item.Name} into your call…", ok: true);
        }
    }

    private void OnMirrorToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingUi)
            return;
        bool enabled = MirrorCheck.IsChecked == true;
        _controller.Settings.VoiceAlsoSpeakers = enabled;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();

        if (!enabled && _voicePlayer.IsPlaying)
        {
            // Cut the speakers copy immediately — this is the echo escape hatch.
            _voicePlayer.StopMirror();
            ShowSaveStatus("Speaker copy silenced — your call still hears it.", ok: true);
        }
        else if (enabled && _voicePlayer.IsPlaying)
        {
            ShowSaveStatus("Speakers will join from the next playback.", ok: true);
        }
    }

    private void OnVoiceVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VoiceVolumeText == null || _loadingUi)
            return;
        int volume = (int)VoiceVolumeSlider.Value;
        VoiceVolumeText.Text = $"{volume}%";
        _controller.Settings.VoiceVolume = volume;
        _voicePlayer.SetVolume(volume / 100f);
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    // ---------- window chrome ----------

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing hides to tray; the app exits from the tray menu.
        e.Cancel = true;
        Hide();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
}
