using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Ferry;

public static partial class LinkifiedText
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(LinkifiedText),
            new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

    [GeneratedRegex(@"(https?://[^\s<]+)")]
    private static partial Regex UrlRegex();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;

        var parts = UrlRegex().Split(text);
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (part.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var link = new Hyperlink(new Run(part))
                {
                    NavigateUri = new Uri(part),
                    ToolTip = part,
                };
                link.RequestNavigate += (_, args) =>
                {
                    var url = args.Uri.AbsoluteUri;
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            App.Log($"link open failed: {ex.Message}");
                        }
                    }
                };
                tb.Inlines.Add(link);
            }
            else
            {
                tb.Inlines.Add(new Run(part));
            }
        }
    }
}
