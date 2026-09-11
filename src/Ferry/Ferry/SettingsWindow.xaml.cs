using System.Windows;
using System.Windows.Controls;

namespace Ferry;

public sealed partial class SettingsWindow : Window
{
    private readonly FerryClient _client;
    private readonly Func<IReadOnlyList<FerryMessage>> _getThreadMessages;
    private readonly Action<string> _onDeviceNameChanged;
    private readonly Action _onTokenRotated;
    private Button? _activeThemeBtn;
    private DeviceIdentity _device;

    public SettingsWindow(
        FerryClient client,
        DeviceIdentity device,
        Func<IReadOnlyList<FerryMessage>> getThreadMessages,
        Action<string> onDeviceNameChanged,
        Action onTokenRotated)
    {
        InitializeComponent();
        _client = client;
        _device = device;
        _getThreadMessages = getThreadMessages;
        _onDeviceNameChanged = onDeviceNameChanged;
        _onTokenRotated = onTokenRotated;

        SourceInitialized += (_, _) => WindowEffects.Apply(this, App.IsDark);
        CloseButton.Click += (_, _) => Close();
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
        };

        // Theme buttons
        ThemeSystemBtn.Click += (_, _) => SetTheme("system", ThemeSystemBtn);
        ThemeLightBtn.Click += (_, _) => SetTheme("light", ThemeLightBtn);
        ThemeDarkBtn.Click += (_, _) => SetTheme("dark", ThemeDarkBtn);
        HighlightCurrentTheme();

        // Device name
        DeviceNameBox.Text = _device.Name;
        SaveNameButton.Click += (_, _) => SaveDeviceName();

        // Security
        RotateTokenButton.Click += async (_, _) => await RotateTokenAsync();
        PairPhoneButton.Click += (_, _) =>
        {
            new ConnectWindow { Owner = this }.ShowDialog();
        };

        // Storage & cleanup
        CleanupButton.Click += async (_, _) =>
        {
            var dialog = new CleanupWindow(_getThreadMessages()) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedDays > 0)
            {
                try
                {
                    var result = await _client.CleanupAsync(dialog.SelectedDays);
                    await RefreshStorageAsync();
                }
                catch (Exception) { }
            }
        };

        Loaded += async (_, _) =>
        {
            await RefreshStorageAsync();
            await RefreshAuthStatusAsync();
        };
    }

    private void HighlightCurrentTheme()
    {
        var btn = App.ThemeMode switch
        {
            "light" => ThemeLightBtn,
            "dark" => ThemeDarkBtn,
            _ => ThemeSystemBtn,
        };
        SetActiveThemeButton(btn);
    }

    private void SetTheme(string mode, Button btn)
    {
        SetActiveThemeButton(btn);
        App.SetThemeMode(mode);
    }

    private void SetActiveThemeButton(Button btn)
    {
        if (_activeThemeBtn is not null)
        {
            _activeThemeBtn.SetResourceReference(BackgroundProperty, "Surface3");
            _activeThemeBtn.SetResourceReference(BorderBrushProperty, "Border");
        }
        _activeThemeBtn = btn;
        _activeThemeBtn.SetResourceReference(BackgroundProperty, "AccentSoft");
        _activeThemeBtn.SetResourceReference(BorderBrushProperty, "Accent");
    }

    private void SaveDeviceName()
    {
        var newName = DeviceNameBox.Text.Trim();
        if (string.IsNullOrEmpty(newName) || newName == _device.Name) return;
        _device = new DeviceIdentity(_device.Id, newName);
        _device.Save();
        _onDeviceNameChanged(newName);
        SaveNameButton.Content = "Saved!";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveNameButton.Content = "Save";
        };
        timer.Start();
    }

    private async Task RefreshAuthStatusAsync()
    {
        try
        {
            var info = await _client.GetInfoAsync();
            AuthStatusText.Text = "Paired";
        }
        catch
        {
            AuthStatusText.Text = "Unavailable";
        }
    }

    private async Task RotateTokenAsync()
    {
        var confirm = new DeleteWindow(1, 0)
        {
            Owner = this,
            Title = "Rotate pairing token?",
        };
        confirm.TitleLine.Text = "Rotate pairing token?";
        confirm.BodyLine.Text = "Signs out paired devices until they scan the new QR.";
        confirm.DeleteButton.Content = "Rotate";
        confirm.FileOption.Visibility = Visibility.Collapsed;

        if (confirm.ShowDialog() == true)
        {
            try
            {
                await _client.RotateTokenAsync();
                _onTokenRotated();
                // Open ConnectWindow for re-pairing
                new ConnectWindow { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to rotate token: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task RefreshStorageAsync()
    {
        try
        {
            var s = await _client.GetStorageAsync();
            UpdateStorageUI(s);
        }
        catch { }
    }

    public void UpdateStorageUI(StorageStats s)
    {
        StorageUsedText.Text = FormatBytes(s.FileBytes);
        StorageLimitText.Text = $"of {FormatBytes(s.LimitBytes)}";
        StorageCountsText.Text = $"{s.FileCount} files · {s.MessageCount} messages";
        var pct = s.LimitBytes > 0 ? Math.Min(100, (double)s.FileBytes / s.LimitBytes * 100) : 0;
        StorageBar.Value = pct;
        if (s.OverLimit)
            StorageBar.SetResourceReference(ForegroundProperty, "Danger");
        else
            StorageBar.SetResourceReference(ForegroundProperty, "Signal");
    }

    private static string FormatBytes(long n)
    {
        if (n <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = (int)Math.Floor(Math.Log(n) / Math.Log(1024));
        if (i == 0) return $"{n} {units[0]}";
        var value = n / Math.Pow(1024, i);
        return $"{value:F1} {units[i]}";
    }
}
