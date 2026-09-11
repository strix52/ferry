using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace Ferry;

public partial class ConnectWindow : Window
{
    private string _pairingUrl = "";

    public ConnectWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowEffects.Apply(this, App.IsDark);
        Loaded += async (_, _) => await LoadAsync();
        CloseButton.Click += (_, _) => Close();
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
        };
        CopyButton.Click += (_, _) =>
        {
            if (_pairingUrl.Length > 0) Clipboard.SetText(_pairingUrl);
        };
    }

    private async Task LoadAsync()
    {
        FerryInfo? info;
        try
        {
            using var client = new FerryClient(App.Log, FerryEndpoint.BaseAddress);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            info = await client.GetInfoAsync(cts.Token);
        }
        catch (Exception ex)
        {
            App.Log($"connect info failed: {ex.Message}");
            UrlBox.Text = "Ferry is unavailable.";
            return;
        }
        if (info?.Primary is not { Length: > 0 })
        {
            UrlBox.Text = "No LAN address.";
            return;
        }
        _pairingUrl = info.Primary;
        UrlBox.Text = Shorten(info.Primary);
        QrImage.Source = RenderQr(info.Primary);
        var rest = info.Urls.Where(u => u != info.Primary).ToList();
        AltText.Text = rest.Count > 0
            ? "Also: " + string.Join(" · ", rest.Select(Shorten))
            : "";
    }

    private static BitmapImage RenderQr(string payload)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qr = new QRCode(data);
        using var bitmap = qr.GetGraphic(8);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string Shorten(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Authority : url;
}
