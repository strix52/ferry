using System.Windows;
using System.Windows.Controls;

namespace Ferry;

public sealed partial class CleanupWindow : Window
{
    public int SelectedDays { get; private set; }

    private readonly IReadOnlyList<FerryMessage> _messages;
    private Button? _activeButton;

    public CleanupWindow(IReadOnlyList<FerryMessage> messages)
    {
        InitializeComponent();
        _messages = messages;
        SourceInitialized += (_, _) => WindowEffects.Apply(this, App.IsDark);

        Days7Btn.Click += (_, _) => SelectPreset(Days7Btn, 7);
        Days15Btn.Click += (_, _) => SelectPreset(Days15Btn, 15);
        Days30Btn.Click += (_, _) => SelectPreset(Days30Btn, 30);
        CustomBtn.Click += (_, _) =>
        {
            SetActiveButton(CustomBtn);
            CustomDaysBox.Visibility = Visibility.Visible;
            CustomDaysBox.Focus();
            ParseCustomDays();
        };

        CustomDaysBox.TextChanged += (_, _) => ParseCustomDays();

        CleanupButton.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => CancelButton.Focus();

        // Default to 30 days preset
        SelectPreset(Days30Btn, 30);
    }

    private void SelectPreset(Button btn, int days)
    {
        SetActiveButton(btn);
        CustomDaysBox.Visibility = Visibility.Collapsed;
        SelectedDays = days;
        UpdateImpact();
    }

    private void SetActiveButton(Button btn)
    {
        if (_activeButton is not null)
        {
            _activeButton.SetResourceReference(BackgroundProperty, "Surface3");
            _activeButton.SetResourceReference(BorderBrushProperty, "Border");
        }
        _activeButton = btn;
        _activeButton.SetResourceReference(BackgroundProperty, "AccentSoft");
        _activeButton.SetResourceReference(BorderBrushProperty, "Accent");
    }

    private void ParseCustomDays()
    {
        if (int.TryParse(CustomDaysBox.Text.Trim(), out var days) && days > 0 && days <= 3650)
        {
            SelectedDays = days;
            UpdateImpact();
        }
        else
        {
            SelectedDays = 0;
            ImpactLine.Text = "Enter 1–3650 days.";
            CleanupButton.IsEnabled = false;
        }
    }

    private void UpdateImpact()
    {
        if (SelectedDays <= 0)
        {
            ImpactLine.Text = "Choose an age.";
            CleanupButton.IsEnabled = false;
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)SelectedDays * 86400000;
        int count = 0;
        long bytes = 0;
        foreach (var m in _messages)
        {
            if (!m.IsText && !m.Deleted && m.CreatedAt < cutoff)
            {
                count++;
                bytes += m.Size ?? 0;
            }
        }

        if (count == 0)
        {
            ImpactLine.Text = $"No files older than {SelectedDays} days.";
            CleanupButton.IsEnabled = false;
        }
        else
        {
            ImpactLine.Text = $"{count} file{(count == 1 ? "" : "s")} · {FormatBytes(bytes)}";
            CleanupButton.IsEnabled = true;
        }
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
