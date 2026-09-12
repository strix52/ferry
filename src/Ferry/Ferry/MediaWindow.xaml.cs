using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Ferry;

// Every image in the thread, newest first, as a grid. Clicking one hands the
// whole loaded run to the existing viewer so its arrow keys still page.
public partial class MediaWindow : Window
{
    // A tile is 150px wide; decoding at 320 keeps it crisp at 200% without
    // holding the full-resolution bitmap. Deliberately not ThumbnailCache:
    // that one is sized for the 180px thread bubble and clears itself whole
    // at 240 entries, so a big gallery would evict the thread's thumbnails
    // and make the main window re-download them.
    private const int TileDecodeWidth = 320;
    private const int PageSize = 120;

    public sealed record Tile(FerryMessage Message, BitmapImage? Image, string Caption);

    private readonly Func<IReadOnlyList<FerryMessage>> _source;
    private readonly Func<FerryMessage, Task>? _saveAsHandler;
    private readonly ObservableCollection<Tile> _tiles = [];
    private List<FerryMessage> _images = [];
    private int _shown;

    public MediaWindow(
        Func<IReadOnlyList<FerryMessage>> source,
        Window? owner = null,
        Func<FerryMessage, Task>? saveAsHandler = null)
    {
        InitializeComponent();
        _source = source;
        _saveAsHandler = saveAsHandler;
        if (owner != null) Owner = owner;

        Tiles.ItemsSource = _tiles;

        SourceInitialized += (_, _) => WindowEffects.Apply(this, App.IsDark);
        Loaded += (_, _) => Reload();
        // Photos that arrived while the grid was in the background show up when
        // he comes back to it, without re-decoding tiles that have not changed.
        Activated += (_, _) => RefreshIfStale();

        CloseButton.Click += (_, _) => Close();
        LoadOlderButton.Click += (_, _) => ShowMore();
        HeaderBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    // Newest first. Id is the insertion order the server hands out, so it
    // orders correctly even when two files share a millisecond.
    internal static List<FerryMessage> SelectImages(IReadOnlyList<FerryMessage> messages) =>
        messages.Where(m => m.ShowThumb && !m.Deleted)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .ToList();

    // Count plus newest id is enough: images only ever arrive at the top, and a
    // delete changes the count. Comparing the whole list would cost more than
    // it saves.
    internal static bool IsStale(IReadOnlyList<FerryMessage> shown, IReadOnlyList<FerryMessage> fresh) =>
        shown.Count != fresh.Count || (fresh.Count > 0 && shown[0].Id != fresh[0].Id);

    private void RefreshIfStale()
    {
        if (IsStale(_images, SelectImages(_source()))) Reload();
    }

    private void Reload()
    {
        // Keep however much he had paged into view, so a refresh does not throw
        // him back to the first page.
        var keep = Math.Max(_shown, PageSize);
        _images = SelectImages(_source());
        _tiles.Clear();
        _shown = 0;
        ShowMore(keep);
    }

    private void ShowMore(int count = PageSize)
    {
        var next = Math.Min(_shown + count, _images.Count);
        for (var i = _shown; i < next; i++)
        {
            var m = _images[i];
            _tiles.Add(new Tile(m, Decode(m.DownloadUrl, m.Id), Caption(m)));
        }
        _shown = next;

        EmptyText.Visibility = _images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadOlderButton.Visibility = _shown < _images.Count ? Visibility.Visible : Visibility.Collapsed;
        LoadOlderButton.Content = $"Load older ({_images.Count - _shown} left)";
        CountText.Text = _images.Count switch
        {
            0 => "",
            1 => "1 photo",
            _ => _shown < _images.Count
                ? $"{_shown} of {_images.Count} photos"
                : $"{_images.Count} photos",
        };
    }

    internal static string Caption(FerryMessage m)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedAt).ToLocalTime();
        return $"{m.Filename}\n{m.DisplayName} · {when:d MMM yyyy, HH:mm} · {m.SizeText}";
    }

    private static BitmapImage? Decode(string url, int id)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = TileDecodeWidth;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch (Exception ex)
        {
            // A tile that will not decode is a cosmetic loss, so leave the
            // placeholder and keep the grid intact.
            App.Log($"media tile {id} failed: {ex.Message}");
            return null;
        }
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Tile tile }) return;
        // Hand over the tiles actually on screen, so the viewer's arrows walk
        // the same run the grid shows rather than jumping into unloaded pages.
        var loaded = _tiles.Select(t => t.Message).ToList();
        var index = loaded.FindIndex(m => m.Id == tile.Message.Id);
        new ImageWindow(loaded, index < 0 ? 0 : index, this, _saveAsHandler).Show();
    }
}
