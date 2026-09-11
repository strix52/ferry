using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ferry;

public partial class ImageWindow : Window
{
    public sealed record ImageItem(string Url, string? Filename, string? SizeText, FerryMessage? Message = null);

    private readonly IReadOnlyList<ImageItem> _items;
    private readonly Func<FerryMessage, Task>? _saveAsHandler;
    private int _currentIndex;
    private BitmapImage? _currentBitmap;
    private Point _lastDragPoint;
    private bool _isDragging;
    private readonly DispatcherTimer _hideTimer;

    public ImageWindow(
        IReadOnlyList<ImageItem> items,
        int initialIndex,
        Window? owner = null,
        Func<FerryMessage, Task>? saveAsHandler = null)
    {
        InitializeComponent();
        _items = items;
        _currentIndex = Math.Clamp(initialIndex, 0, Math.Max(0, items.Count - 1));
        _saveAsHandler = saveAsHandler;
        if (owner != null) Owner = owner;

        SourceInitialized += (_, _) =>
        {
            WindowEffects.Apply(this, App.IsDark);
            PositionWindow(owner);
        };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hideTimer.Tick += (_, _) =>
        {
            if (!CaptionStrip.IsMouseOver)
            {
                CaptionStrip.Visibility = Visibility.Collapsed;
                _hideTimer.Stop();
            }
        };

        Loaded += (_, _) =>
        {
            LoadCurrent();
            _hideTimer.Start();
        };

        MouseMove += (_, _) =>
        {
            if (CaptionStrip.Visibility != Visibility.Visible)
                CaptionStrip.Visibility = Visibility.Visible;
            _hideTimer.Stop();
            _hideTimer.Start();
        };

        CloseButton.Click += (_, _) => Close();
        SaveButton.Click += async (_, _) => await SaveCurrentAsync();
        PrevButton.Click += (_, _) => Navigate(-1);
        NextButton.Click += (_, _) => Navigate(1);

        CaptionStrip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var current = dep;
                while (current != null && current != CaptionStrip)
                {
                    if (current is System.Windows.Controls.Primitives.ButtonBase)
                        return;
                    current = VisualTreeHelper.GetParent(current);
                }
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    DragMove();
                }
            }
        };

        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                Navigate(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                Navigate(1);
                e.Handled = true;
            }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                CopyCurrent();
                e.Handled = true;
            }
            else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                await SaveCurrentAsync();
                e.Handled = true;
            }
        };

        ViewportCanvas.MouseWheel += (_, e) =>
        {
            if (_currentBitmap == null) return;
            var pos = e.GetPosition(ViewportCanvas);
            double zoom = e.Delta > 0 ? 1.15 : (1.0 / 1.15);
            double currentScale = ImageScale.ScaleX;
            double targetScale = Math.Clamp(currentScale * zoom, 0.1, 8.0);
            double factor = targetScale / currentScale;

            ImageTranslate.X = pos.X - (pos.X - ImageTranslate.X) * factor;
            ImageTranslate.Y = pos.Y - (pos.Y - ImageTranslate.Y) * factor;
            ImageScale.ScaleX = targetScale;
            ImageScale.ScaleY = targetScale;
        };

        ViewportCanvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ToggleFit();
                return;
            }
            _isDragging = true;
            _lastDragPoint = e.GetPosition(this);
            ViewportCanvas.CaptureMouse();
        };

        ViewportCanvas.MouseMove += (_, e) =>
        {
            if (_isDragging)
            {
                var current = e.GetPosition(this);
                var delta = current - _lastDragPoint;
                _lastDragPoint = current;
                ImageTranslate.X += delta.X;
                ImageTranslate.Y += delta.Y;
            }
        };

        ViewportCanvas.MouseLeftButtonUp += (_, _) =>
        {
            if (_isDragging)
            {
                _isDragging = false;
                ViewportCanvas.ReleaseMouseCapture();
            }
        };

        SizeChanged += (_, _) =>
        {
            if (_currentBitmap != null && ImageScale.ScaleX <= 1.0)
                FitImage();
        };
    }

    public ImageWindow(string url, string? filename, Window? owner = null)
        : this([new ImageItem(url, filename, null)], 0, owner ?? Application.Current.MainWindow) { }

    public ImageWindow(
        IReadOnlyList<FerryMessage> images,
        int initialIndex,
        Window? owner = null,
        Func<FerryMessage, Task>? saveAsHandler = null)
        : this(
            images.Select(m => new ImageItem(m.DownloadUrl, m.Filename, m.SizeText, m)).ToList(),
            initialIndex,
            owner,
            saveAsHandler) { }

    private void PositionWindow(Window? owner)
    {
        try
        {
            var bounds = ViewerPlacementHelper.ResolveBounds(
                getScreenMetrics: () =>
                {
                    var targetOwner = owner ?? Application.Current?.MainWindow;
                    System.Windows.Forms.Screen? screen = null;
                    if (targetOwner != null && targetOwner.IsLoaded)
                    {
                        var hwnd = new WindowInteropHelper(targetOwner).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            screen = System.Windows.Forms.Screen.FromHandle(hwnd);
                        }
                    }
                    screen ??= System.Windows.Forms.Screen.PrimaryScreen;
                    if (screen != null && screen.WorkingArea.Width > 0 && screen.WorkingArea.Height > 0)
                    {
                        var area = screen.WorkingArea;
                        var dpi = (targetOwner != null && targetOwner.IsLoaded)
                            ? VisualTreeHelper.GetDpi(targetOwner)
                            : VisualTreeHelper.GetDpi(this);
                        return (area.Left, area.Top, area.Width, area.Height, dpi.DpiScaleX, dpi.DpiScaleY);
                    }
                    return null;
                },
                getWorkAreaMetrics: () =>
                {
                    var wa = SystemParameters.WorkArea;
                    return (wa.Width > 0 && wa.Height > 0) ? (wa.Left, wa.Top, wa.Width, wa.Height) : null;
                });

            if (bounds.HasValue)
            {
                ApplyBounds(bounds.Value);
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log($"PositionWindow fallback error: {ex.Message}");
        }

        // Terminal path: no valid work area could be determined; fail closed to avoid unconstrained or off-screen window
        App.Log("PositionWindow: no valid work area available; failing closed.");
        Close();
    }

    private void ApplyBounds(WindowBounds bounds)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void Navigate(int delta)
    {
        int newIndex = _currentIndex + delta;
        if (newIndex >= 0 && newIndex < _items.Count)
        {
            _currentIndex = newIndex;
            LoadCurrent();
        }
    }

    private void LoadCurrent()
    {
        if (_items.Count == 0) return;
        var item = _items[_currentIndex];
        FilenameText.Text = item.Filename ?? "Image";
        SizeText.Text = item.SizeText ?? "";
        IndexText.Text = _items.Count > 1 ? $"{_currentIndex + 1} of {_items.Count}" : "";

        PrevButton.Visibility = _currentIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _currentIndex < _items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(item.Url);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            // The url is http, so WPF fetches it on a background download and
            // the bitmap is neither freezable nor measurable until the bytes
            // land — PixelWidth reads 0 and Freeze() throws. Size and fit once
            // the download reports in; an already-cached image never does.
            _currentBitmap = bitmap;
            DisplayImage.Source = bitmap;
            if (bitmap.IsDownloading)
            {
                bitmap.DownloadCompleted += (_, _) => ShowNaturalSize(bitmap);
                bitmap.DownloadFailed += (_, e) =>
                    App.Log($"image viewer download failed: {e.ErrorException?.Message}");
            }
            else
            {
                ShowNaturalSize(bitmap);
            }
        }
        catch (Exception ex)
        {
            App.Log($"image viewer decode error: {ex.Message}");
            DisplayImage.Source = null;
            _currentBitmap = null;
        }
    }

    // Guarded against a download that lands after the user has already arrowed
    // on to the next image: the stale bitmap must not resize the viewport.
    private void ShowNaturalSize(BitmapImage bitmap)
    {
        if (!ReferenceEquals(_currentBitmap, bitmap)) return;
        DisplayImage.Width = bitmap.PixelWidth;
        DisplayImage.Height = bitmap.PixelHeight;
        FitImage();
    }

    private void FitImage()
    {
        if (_currentBitmap == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        double w = _currentBitmap.PixelWidth;
        double h = _currentBitmap.PixelHeight;
        if (w <= 0 || h <= 0) return;

        double availW = Math.Max(100, ActualWidth - 40);
        double availH = Math.Max(100, ActualHeight - 60);

        double scale = Math.Min(1.0, Math.Min(availW / w, availH / h));
        ImageScale.ScaleX = scale;
        ImageScale.ScaleY = scale;

        ImageTranslate.X = (ActualWidth - w * scale) / 2;
        ImageTranslate.Y = (ActualHeight - h * scale) / 2;
    }

    private void ToggleFit()
    {
        if (_currentBitmap == null) return;
        if (Math.Abs(ImageScale.ScaleX - 1.0) < 0.05)
        {
            FitImage();
        }
        else
        {
            ImageScale.ScaleX = 1.0;
            ImageScale.ScaleY = 1.0;
            ImageTranslate.X = (ActualWidth - _currentBitmap.PixelWidth) / 2;
            ImageTranslate.Y = (ActualHeight - _currentBitmap.PixelHeight) / 2;
        }
    }

    private void CopyCurrent()
    {
        if (_currentBitmap == null) return;
        try
        {
            Clipboard.SetImage(_currentBitmap);
        }
        catch (Exception ex)
        {
            App.Log($"clipboard copy image failed: {ex.Message}");
        }
    }

    private async Task SaveCurrentAsync()
    {
        if (_items.Count == 0) return;
        var item = _items[_currentIndex];
        if (item.Message != null && _saveAsHandler != null)
        {
            await _saveAsHandler(item.Message);
        }
        else
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = item.Filename ?? "image.png" };
            if (dialog.ShowDialog() == true && _currentBitmap != null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_currentBitmap));
                using var stream = File.Create(dialog.FileName);
                encoder.Save(stream);
            }
        }
    }
}
