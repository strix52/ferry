using System.IO;
using System.Windows;

namespace Ferry;

public partial class App : Application
{
    // Windows theme, as last applied. Windows (not Ferry) owns this choice;
    // WindowEffects reads it to pick the DWM caption colour, and the flag is
    // static so a window created later still gets the right chrome.
    public static bool IsDark { get; private set; }

    public static new string ThemeMode { get; private set; } = "system";

    // Raised on the UI thread after the palette has been swapped, so open
    // windows can re-apply the non-WPF parts of their chrome.
    public static event Action? ThemeChanged;

    private bool _themeApplied;

    public static void SetThemeMode(string mode)
    {
        ThemeMode = mode.ToLowerInvariant();
        if (Current is App app)
        {
            var dark = ThemeMode switch
            {
                "dark" => true,
                "light" => false,
                _ => DetectDark(),
            };
            app.ApplyTheme(dark, force: true);
        }
    }

    public static string? StartupWarning { get; private set; }
    private Ferry.Server.FerryServerInstance? _server;

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("startup");
        FerryEndpoint.ResolveFromEnvironment();

        var port = FerryEndpoint.Port;
        var envDataDir = Environment.GetEnvironmentVariable("FERRY_DATA_DIR");
        string dataDir;
        if (!string.IsNullOrEmpty(envDataDir))
        {
            dataDir = Path.GetFullPath(envDataDir);
        }
        else
        {
            var cwdData = Path.Combine(Directory.GetCurrentDirectory(), "data");
            var localAppDataFerryData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry", "data");
            var appData = Path.Combine(AppContext.BaseDirectory, "data");
            dataDir = Directory.Exists(cwdData) ? cwdData
                : (Directory.Exists(localAppDataFerryData) || !Directory.Exists(appData)) ? localAppDataFerryData
                : appData;
        }

        var envPublicDir = Environment.GetEnvironmentVariable("FERRY_PUBLIC_DIR");
        string publicDir;
        if (!string.IsNullOrEmpty(envPublicDir))
        {
            publicDir = Path.GetFullPath(envPublicDir);
        }
        else
        {
            var appPublic = Path.Combine(AppContext.BaseDirectory, "public");
            var cwdPublic = Path.Combine(Directory.GetCurrentDirectory(), "public");
            publicDir = Directory.Exists(appPublic) ? appPublic : cwdPublic;
        }

        if (!Directory.Exists(publicDir))
        {
            Log($"public dir missing: {publicDir}");
            StartupWarning = $"Phone client missing: {publicDir}";
        }

        var config = new Ferry.Server.FerryServerConfig
        {
            Port = port,
            DataDir = dataDir,
            PublicDir = publicDir,
        };

        try
        {
            _server = Task.Run(() => Ferry.Server.FerryServerInstance.StartAsync(config)).GetAwaiter().GetResult();
            Log($"server started on port {port}");
        }
        catch (Exception ex)
        {
            Log($"server start failed: {ex.Message}");
            StartupWarning ??= $"Server start failed: {ex.Message}";
        }

        ApplyTheme(DetectDark());
        // Windows raises this off the UI thread whenever the user flips
        // light/dark (among other things), so the app follows the system live
        // instead of only at launch.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"UI-thread: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log($"fatal: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"unobserved-task: {args.Exception}");
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_server != null)
        {
            try
            {
                Task.Run(async () =>
                {
                    await _server.StopAsync();
                    await _server.DisposeAsync();
                }).GetAwaiter().GetResult();
                Log("server stopped");
            }
            catch (Exception ex)
            {
                Log($"server stop failed: {ex.Message}");
            }
        }

        // SystemEvents holds the handler in a static list on a background
        // thread; leaving it hooked keeps the app object alive past shutdown.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnExit(e);
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        if (ThemeMode == "system")
        {
            Dispatcher.BeginInvoke(() => ApplyTheme(DetectDark()));
        }
    }

    // Reads Settings > Personalization > Colors. A missing key (fresh
    // profiles, some RDP sessions) falls back to light.
    private static bool DetectDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch (Exception ex) { Log($"theme detect failed: {ex.Message}"); }
        return false;
    }

    public void ApplyTheme(bool dark, bool force = false)
    {
        if (!force && _themeApplied && dark == IsDark) return;
        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
            };
            // Replace the palette in place. Clearing the collection would take
            // the control library with it and leave every control unstyled.
            if (Resources.MergedDictionaries.Count > 0) Resources.MergedDictionaries[0] = dict;
            else Resources.MergedDictionaries.Add(dict);
            IsDark = dark;
            _themeApplied = true;
            Log(dark ? "theme dark" : "theme light");
            ThemeChanged?.Invoke();
        }
        catch (Exception ex) { Log($"theme apply failed: {ex.Message}"); }
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ferry-wpf.log"),
                $"{DateTimeOffset.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }
}
