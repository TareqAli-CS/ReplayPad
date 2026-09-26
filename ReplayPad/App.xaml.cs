using System.Windows;
using ReplayPad.Configuration;
using ReplayPad.Core;
using ReplayPad.UI;
using NAudio.MediaFoundation;

namespace ReplayPad;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppController? _controller;
    private TrayIconManager? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureAndMigrate();
        StartupRegistry.MigrateLegacyEntry();
        InstallCrashHandlers();

        _mutex = new Mutex(initiallyOwned: true, "ReplayPad_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "ReplayPad is already running — look for its icon in the system tray.",
                "ReplayPad", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppSettings settings;
        try
        {
            settings = AppSettings.Load();
        }
        catch (Exception ex)
        {
            Logger.Log("Configuration error: " + ex);
            MessageBox.Show(
                "Could not load appsettings.json:\n\n" + ex.Message,
                "ReplayPad", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        MediaFoundationApi.Startup();
        _controller = new AppController(settings);

        try
        {
            _controller.StartCapture();
        }
        catch (Exception ex)
        {
            Logger.Log("Failed to start capture: " + ex);
            MessageBox.Show(
                "Audio capture could not be started:\n\n" + ex.Message +
                "\n\nThe app will stay open — check your audio devices and press Resume.",
                "ReplayPad", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _window = new MainWindow(_controller);
        _tray = new TrayIconManager(_controller, ShowMainWindow, ExitApplication);
        _controller.LauncherRequested += ShowLauncher;

        // While a hotkey field is capturing, global hotkeys must be off —
        // otherwise pressing a bound combo fires it instead of setting it.
        HotkeyBox.CaptureStarted += () => _controller?.SuspendHotkeys();
        HotkeyBox.CaptureEnded += () => _controller?.RegisterHotkey();

        string hotkeyError = _controller.RegisterHotkey();
        if (hotkeyError.Length > 0)
        {
            Logger.Log(hotkeyError);
            _tray.ShowWarning("Hotkey problem", hotkeyError + " You can still save from the window or tray.");
        }

        // "--edit <file>" opens the editor directly (capture still runs).
        int editIndex = Array.IndexOf(e.Args, "--edit");
        if (editIndex >= 0 && editIndex + 1 < e.Args.Length && File.Exists(e.Args[editIndex + 1]))
        {
            new EditorWindow(e.Args[editIndex + 1], settings).Show();
            return;
        }

        if (!e.Args.Contains("--minimized"))
            ShowMainWindow();

        StartAutoBackupTimer();

        // Quiet startup update check: only speaks up when something newer exists.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var result = await UpdateChecker.CheckAsync();
            if (result is { IsNewer: true } update)
                Dispatcher.BeginInvoke(() => _tray?.NotifyUpdateAvailable(update.LatestTag));
        });
    }

    /// <summary>
    /// A background recorder must not vanish because one operation threw.
    /// UI-thread exceptions are logged and swallowed so capture keeps
    /// running; non-recoverable ones are at least logged before the
    /// process dies.
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Log("Unhandled UI exception (app kept running): " + args.Exception);
            _tray?.ShowWarning("Unexpected error",
                "Something went wrong but the app is still recording. Details are in log.txt.");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Log("Fatal unhandled exception: " + args.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Log("Unobserved task exception (ignored): " + args.Exception);
            args.SetObserved();
        };
    }

    private System.Windows.Threading.DispatcherTimer? _backupTimer;
    private bool _autoBackupFailedNotified;

    /// <summary>
    /// Scheduled Google Drive backups: checked shortly after startup, then
    /// every 30 minutes; runs only when due, signed in and nothing else is
    /// running. Stays silent on success, warns once on failure.
    /// </summary>
    private void StartAutoBackupTimer()
    {
        _backupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _backupTimer.Tick += async (_, _) =>
        {
            _backupTimer.Interval = TimeSpan.FromMinutes(30);
            var backup = _controller?.Backup;
            if (backup == null || backup.IsBusy || !backup.IsAutoBackupDue)
                return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromHours(2));
                await backup.BackUpToDriveAsync(null, cts.Token);
                _autoBackupFailedNotified = false;
            }
            catch (Exception ex)
            {
                Logger.Log("Automatic backup failed: " + ex.Message);
                if (!_autoBackupFailedNotified)
                {
                    _autoBackupFailedNotified = true;
                    _tray?.ShowWarning("Automatic backup failed",
                        ex is GoogleSignInRequiredException
                            ? "Sign in to Google again in ☁ Backup & restore."
                            : ex.Message);
                }
            }
        };
        _backupTimer.Start();
    }

    private LauncherWindow? _launcher;

    private void ShowLauncher()
    {
        if (_controller == null)
            return;
        if (_launcher is { IsVisible: true })
        {
            _launcher.Activate();
            return;
        }
        _launcher = new LauncherWindow(_controller);
        _launcher.Closed += (_, _) => _launcher = null;
        _launcher.Show();
    }

    private void ShowMainWindow()
    {
        if (_window == null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApplication()
    {
        _tray?.Dispose();
        _tray = null;
        _controller?.Dispose();
        _controller = null;
        MediaFoundationApi.Shutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _controller?.Dispose();
        base.OnExit(e);
    }
}
