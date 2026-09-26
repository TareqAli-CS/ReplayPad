using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ReplayPad.Core;

namespace ReplayPad.UI;

/// <summary>Google Drive and file backup / restore of the whole library.</summary>
public partial class BackupWindow : Window
{
    private sealed record BackupRow(GoogleDriveClient.BackupFile File, string Title, string Details);

    private readonly AppController _controller;
    private CloudBackup Backup => _controller.Backup;
    private CancellationTokenSource? _cts;
    private bool _loading;

    /// <summary>True when something was restored — the caller refreshes its lists.</summary>
    public bool Restored { get; private set; }

    public BackupWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();

        _loading = true;
        var prefs = Backup.Prefs;
        AutoCheck.IsChecked = prefs.AutoBackup;
        EveryBox.SelectedIndex = prefs.EveryDays >= 7 ? 2 : prefs.EveryDays >= 3 ? 1 : 0;
        KeepBox.SelectedIndex = prefs.KeepCount >= 10 ? 2 : prefs.KeepCount >= 5 ? 1 : 0;
        IncludeReplaysCheck.IsChecked = prefs.IncludeReplays;
        _loading = false;

        Backup.StateChanged += OnBackupStateChanged;
        Closed += (_, _) => Backup.StateChanged -= OnBackupStateChanged;

        UpdateAccountUi();
        UpdateSummary();
        Loaded += async (_, _) =>
        {
            if (Backup.Auth.IsSignedIn)
                await RefreshListAsync();
        };
    }

    private void OnBackupStateChanged() => Dispatcher.BeginInvoke(UpdateAccountUi);

    // ---------- state ----------

    private void UpdateAccountUi()
    {
        bool configured = GoogleAuth.IsConfigured;
        bool signedIn = configured && Backup.Auth.IsSignedIn;
        bool busy = Backup.IsBusy;

        NotConfiguredText.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        AccountRow.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignInBtn.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutBtn.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountText.Text = signedIn
            ? "Signed in as " + (Backup.Auth.Email ?? "your Google account")
            : "Not signed in";

        foreach (var button in new[] { SignInBtn, SignOutBtn, BackUpBtn, SaveFileBtn, RestoreFileBtn })
            button.IsEnabled = !busy;
        RestoreBtn.IsEnabled = !busy && BackupList.SelectedItem != null;
        DeleteBtn.IsEnabled = !busy && BackupList.SelectedItem != null;

        LastBackupText.Text = Backup.Prefs.LastBackupUtc is DateTime last
            ? "Last backup from this PC: " + Friendly(last)
            : "No backup from this PC yet";
    }

    private void UpdateSummary()
    {
        try
        {
            var m = Backup.Preview();
            string replays = Backup.Prefs.IncludeReplays ? ", " + Count(m.ReplayCount, "replay") : "";
            SummaryText.Text = $"{Count(m.SoundCount, "sound")} in {Count(m.CategoryCount, "category", "categories")}" +
                               $"{replays} — {Mb(m.TotalBytes)}";
        }
        catch (Exception ex)
        {
            SummaryText.Text = "";
            Logger.Log("Backup preview failed: " + ex.Message);
        }
    }

    private void OnPrefsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var prefs = Backup.Prefs;
        prefs.AutoBackup = AutoCheck.IsChecked == true;
        prefs.EveryDays = int.Parse((string)((ComboBoxItem)EveryBox.SelectedItem).Tag);
        prefs.KeepCount = int.Parse((string)((ComboBoxItem)KeepBox.SelectedItem).Content);
        prefs.IncludeReplays = IncludeReplaysCheck.IsChecked == true;
        prefs.Save();
        UpdateSummary();
    }

    private void OnBackupSelected(object sender, SelectionChangedEventArgs e) => UpdateAccountUi();

    // ---------- sign-in ----------

    private async void OnSignInClick(object sender, RoutedEventArgs e)
    {
        await RunAsync("Waiting for you to approve in the browser…", async ct =>
        {
            await Backup.SignInAsync(ct);
            ShowStatus("Signed in — you can back up now.", ok: true);
            await RefreshListAsync();
        }, showBar: false);
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Sign out of Google on this PC?\n\nYour backups stay in your Drive; sign in again any time to restore them.",
                "Backup & restore", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await Backup.SignOutAsync();
        BackupList.ItemsSource = null;
        ShowStatus("Signed out.", ok: true);
    }

    // ---------- Drive ----------

    private async Task RefreshListAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var backups = await Backup.Drive.ListBackupsAsync(cts.Token);
            if (Backup.Auth.Email == null)
                Backup.Auth.SetEmail(await Backup.Drive.GetEmailAsync(cts.Token));

            BackupList.ItemsSource = backups.Select(b => new BackupRow(b,
                $"{Friendly(b.CreatedUtc)}  ·  {b.Machine}" +
                (string.Equals(b.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? " (this PC)" : ""),
                $"{Count(b.Sounds, "sound")} · {Count(b.Categories, "category", "categories")} · " +
                $"{Count(b.Replays, "replay")} · {Mb(b.Size)}")).ToList();
            EmptyListText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateAccountUi();
        }
        catch (GoogleSignInRequiredException ex)
        {
            UpdateAccountUi();
            ShowStatus(ex.Message, ok: false);
        }
        catch (Exception ex)
        {
            Logger.Log("Listing Drive backups failed: " + ex);
            ShowStatus("Couldn't load your backups: " + ex.Message, ok: false);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshListAsync();

    private async void OnBackUpClick(object sender, RoutedEventArgs e)
    {
        await RunAsync("Packing…", async ct =>
        {
            var file = await Backup.BackUpToDriveAsync(ProgressReporter(), ct);
            ShowStatus($"Backed up to Google Drive ✔  ({Mb(file.Size)})", ok: true);
            UpdateAccountUi();
            await RefreshListAsync();
        });
    }

    private async void OnRestoreDriveClick(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupRow row || !ConfirmRestore(row.Title,
                $"{row.File.Sounds} sounds, {row.File.Categories} categories, {row.File.Replays} replays"))
            return;
        bool withSettings = RestoreSettingsCheck.IsChecked == true;
        await RunAsync("Downloading…", async ct =>
            ReportRestore(await Backup.RestoreFromDriveAsync(row.File, withSettings, ProgressReporter(), ct)));
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupRow row)
            return;
        if (MessageBox.Show(this, $"Delete this backup from your Google Drive?\n\n{row.Title}\n\nThis can't be undone.",
                "Delete backup", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await RunAsync("Deleting…", async ct =>
        {
            await Backup.Drive.DeleteAsync(row.File.Id, ct);
            ShowStatus("Backup deleted.", ok: true);
            await RefreshListAsync();
        }, showBar: false);
    }

    // ---------- file ----------

    private async void OnSaveFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save ReplayPad backup",
            Filter = "ReplayPad backup (*.zip)|*.zip",
            FileName = $"ReplayPad backup {DateTime.Now:yyyy-MM-dd}.zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await RunAsync("Packing…", async ct =>
        {
            var m = await Backup.SaveToFileAsync(dialog.FileName, ProgressReporter(), ct);
            ShowStatus($"Saved {Path.GetFileName(dialog.FileName)} ✔  ({m.SoundCount} sounds, {m.ReplayCount} replays)", ok: true);
        });
    }

    private async void OnRestoreFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Restore ReplayPad backup",
            Filter = "ReplayPad backup (*.zip)|*.zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        LibraryBackup.Manifest manifest;
        try
        {
            manifest = LibraryBackup.ReadManifest(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, ok: false);
            return;
        }
        if (!ConfirmRestore($"{Friendly(manifest.CreatedUtc)}  ·  {manifest.Machine}",
                $"{manifest.SoundCount} sounds, {manifest.CategoryCount} categories, {manifest.ReplayCount} replays"))
            return;
        bool withSettings = RestoreSettingsCheck.IsChecked == true;
        await RunAsync("Restoring…", async ct =>
            ReportRestore(await Backup.RestoreFromFileAsync(dialog.FileName, withSettings, ProgressReporter(), ct)));
    }

    // ---------- restore helpers ----------

    private bool ConfirmRestore(string title, string contents)
        => MessageBox.Show(this,
               $"Restore this backup?\n\n{title}\n{contents}\n\n" +
               "Missing sounds, categories and replays are added to your library. Nothing is overwritten or " +
               "deleted: files you already have are kept, and labels/colors/hotkeys only fill in where missing." +
               (RestoreSettingsCheck.IsChecked == true ? "\n\nYour settings will also be replaced by the backup's." : ""),
               "Restore backup", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private void ReportRestore(LibraryBackup.RestoreResult result)
    {
        Restored = true;
        UpdateSummary();
        string text = $"Restored ✔  {result.Added} added, {result.AlreadyThere} already here" +
                      (result.Renamed > 0 ? $", {result.Renamed} renamed to avoid overwriting" : "") + ".";
        if (result.Failed.Count > 0)
            text += $" {result.Failed.Count} couldn't be restored (see log.txt).";
        ShowStatus(text, ok: result.Failed.Count == 0);
    }

    // ---------- plumbing ----------

    private IProgress<BackupProgress> ProgressReporter() => new Progress<BackupProgress>(p =>
    {
        Progress.Value = p.Fraction;
        ProgressText.Text = $"{p.Stage}… {p.Fraction:P0}";
    });

    /// <summary>Runs one operation with progress, cancel and friendly errors.</summary>
    private async Task RunAsync(string startText, Func<CancellationToken, Task> work, bool showBar = true)
    {
        if (_cts != null)
            return;
        _cts = new CancellationTokenSource();
        StatusText.Visibility = Visibility.Collapsed;
        ProgressRow.Visibility = Visibility.Visible;
        Progress.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        Progress.Value = 0;
        ProgressText.Text = startText;
        SetBusyUi(true);
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            ShowStatus(ex.Message.StartsWith("The operation was canceled") || ex.Message.StartsWith("A task was canceled")
                ? "Cancelled."
                : ex.Message, ok: false);
        }
        catch (GoogleSignInRequiredException ex)
        {
            ShowStatus(ex.Message, ok: false);
        }
        catch (Exception ex)
        {
            Logger.Log("Backup operation failed: " + ex);
            ShowStatus(ex.Message, ok: false);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ProgressRow.Visibility = Visibility.Collapsed;
            SetBusyUi(false);
            UpdateAccountUi();
        }
    }

    private void SetBusyUi(bool busy)
    {
        foreach (var control in new Control[] { SignInBtn, SignOutBtn, BackUpBtn, SaveFileBtn, RestoreFileBtn,
                                                RestoreBtn, DeleteBtn, IncludeReplaysCheck })
            control.IsEnabled = !busy;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cts == null)
            return;
        e.Cancel = true;
        if (MessageBox.Show(this, "A backup or restore is still running. Cancel it and close?", "Backup & restore",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _cts.Cancel();
        _ = CloseWhenIdleAsync(); // closes once the operation has wound down
    }

    private async Task CloseWhenIdleAsync()
    {
        while (_cts != null)
            await Task.Delay(100);
        Close();
    }

    private void ShowStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(ok ? "GreenBrush" : "WarnBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    private static string Count(int n, string one, string? many = null)
        => $"{n} {(n == 1 ? one : many ?? one + "s")}";

    private static string Mb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
        : $"{bytes / (1024.0 * 1024):0.#} MB";

    private static string Friendly(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? $"Today {local:HH:mm}"
             : local.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {local:HH:mm}"
             : local.ToString("yyyy-MM-dd HH:mm");
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
