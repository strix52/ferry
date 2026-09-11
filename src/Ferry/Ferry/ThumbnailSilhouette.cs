using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ferry;

public static class ThumbnailSilhouette
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(ImageSource),
            typeof(ThumbnailSilhouette),
            new PropertyMetadata(null, OnSourceChanged));

    public static ImageSource? GetSource(DependencyObject obj) => (ImageSource?)obj.GetValue(SourceProperty);
    public static void SetSource(DependencyObject obj, ImageSource? value) => obj.SetValue(SourceProperty, value);

    private static readonly DependencyProperty ElementLoadedProperty =
        DependencyProperty.RegisterAttached(
            "ElementLoaded",
            typeof(bool?),
            typeof(ThumbnailSilhouette),
            new PropertyMetadata(null));

    internal static void SetLoadedForTesting(FrameworkElement element, bool isLoaded)
    {
        element.SetValue(ElementLoadedProperty, isLoaded);
    }

    private static readonly DependencyProperty ActiveManagerProperty =
        DependencyProperty.RegisterAttached(
            "ActiveManager",
            typeof(RegistrationManager),
            typeof(ThumbnailSilhouette),
            new PropertyMetadata(null));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        // Clean up previous registration manager
        if (element.GetValue(ActiveManagerProperty) is RegistrationManager prev)
        {
            prev.Dispose();
            element.SetValue(ActiveManagerProperty, null);
        }

        if (e.NewValue is ImageSource source)
        {
            var manager = new RegistrationManager(element, source);
            element.SetValue(ActiveManagerProperty, manager);
            manager.Evaluate();
        }
        else
        {
            ApplyDefaultSilhouette(element);
        }
    }

    private static void ApplyDefaultSilhouette(FrameworkElement element)
    {
        element.Width = double.NaN;
        element.MaxWidth = 380.0;
        element.MaxHeight = 220.0;
    }

    internal static void ApplySilhouette(FrameworkElement element, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            ApplyDefaultSilhouette(element);
            return;
        }

        element.MaxHeight = 220.0;

        // Portrait: height noticeably exceeds width (> 1.05)
        if (pixelHeight > pixelWidth * 1.05)
        {
            element.Width = 180.0;
            element.MaxWidth = 180.0;
        }
        // Square-ish: within 5%
        else if (Math.Abs(pixelHeight - pixelWidth) <= pixelWidth * 0.05)
        {
            element.Width = 220.0;
            element.MaxWidth = 220.0;
        }
        // Landscape: responsive up to 380 DIPs, flexes down on narrow window widths
        else
        {
            element.Width = double.NaN;
            element.MaxWidth = 380.0;
        }
    }

    internal static RegistrationManager CreateManagerForTesting(
        FrameworkElement element,
        ImageSource source,
        ISilhouetteSource silhouetteSource)
    {
        element.SetValue(SourceProperty, source);
        if (element.GetValue(ActiveManagerProperty) is RegistrationManager prev)
        {
            prev.Dispose();
            element.SetValue(ActiveManagerProperty, null);
        }

        var manager = new RegistrationManager(element, source, silhouetteSource);
        element.SetValue(ActiveManagerProperty, manager);
        manager.Evaluate();
        return manager;
    }

    internal interface ISilhouetteSource
    {
        bool IsDownloading { get; }
        int PixelWidth { get; }
        int PixelHeight { get; }
        event EventHandler? Completed;
        event EventHandler? Failed;
    }

    internal sealed class BitmapImageSilhouetteSource : ISilhouetteSource
    {
        private readonly BitmapImage _bi;
        private EventHandler? _failed;

        public BitmapImageSilhouetteSource(BitmapImage bi) => _bi = bi;
        public bool IsDownloading => _bi.IsDownloading;
        public int PixelWidth => _bi.PixelWidth;
        public int PixelHeight => _bi.PixelHeight;

        public event EventHandler? Completed
        {
            add => _bi.DownloadCompleted += value;
            remove => _bi.DownloadCompleted -= value;
        }

        public event EventHandler? Failed
        {
            add
            {
                if (_failed == null) _bi.DownloadFailed += OnBiFailed;
                _failed += value;
            }
            remove
            {
                _failed -= value;
                if (_failed == null) _bi.DownloadFailed -= OnBiFailed;
            }
        }

        private void OnBiFailed(object? sender, ExceptionEventArgs e) => _failed?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class StaticBitmapSilhouetteSource : ISilhouetteSource
    {
        private readonly BitmapSource _bs;
        public StaticBitmapSilhouetteSource(BitmapSource bs) => _bs = bs;
        public bool IsDownloading => false;
        public int PixelWidth => _bs.PixelWidth;
        public int PixelHeight => _bs.PixelHeight;
        public event EventHandler? Completed { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
    }

    internal sealed class RegistrationManager : IDisposable
    {
        private readonly WeakReference<FrameworkElement> _elementRef;
        private readonly WeakReference<ImageSource> _sourceRef;
        private readonly ISilhouetteSource? _silhouetteSource;
        private bool _isLoaded;
        private int _loadGeneration;
        private bool _downloadHooked;
        private bool _disposed;

        internal bool IsHooked => _downloadHooked;
        internal bool IsLoadedState => _isLoaded;

        public RegistrationManager(
            FrameworkElement element,
            ImageSource source,
            ISilhouetteSource? silhouetteSource = null)
        {
            _elementRef = new WeakReference<FrameworkElement>(element);
            _sourceRef = new WeakReference<ImageSource>(source);
            _isLoaded = (bool?)element.GetValue(ElementLoadedProperty) ?? element.IsLoaded;

            if (silhouetteSource != null)
            {
                _silhouetteSource = silhouetteSource;
            }
            else if (source is BitmapImage bi)
            {
                _silhouetteSource = new BitmapImageSilhouetteSource(bi);
            }
            else if (source is BitmapSource bs)
            {
                _silhouetteSource = new StaticBitmapSilhouetteSource(bs);
            }

            element.Loaded += OnElementLoaded;
            element.Unloaded += OnElementUnloaded;
        }

        public void Evaluate()
        {
            if (_disposed) return;
            if (!_elementRef.TryGetTarget(out var element)) return;
            if (!_sourceRef.TryGetTarget(out var source)) return;

            // Guard against ListBox recycling
            if (!ReferenceEquals(GetSource(element), source)) return;

            if (_silhouetteSource != null)
            {
                if (_silhouetteSource.IsDownloading)
                {
                    ApplyDefaultSilhouette(element);
                    if (_isLoaded)
                    {
                        HookDownload();
                    }
                    else
                    {
                        UnhookDownload();
                    }
                }
                else
                {
                    UnhookDownload();
                    ApplySilhouette(element, _silhouetteSource.PixelWidth, _silhouetteSource.PixelHeight);
                }
            }
            else
            {
                UnhookDownload();
                ApplyDefaultSilhouette(element);
            }
        }

        private void HookDownload()
        {
            if (_downloadHooked || _silhouetteSource == null || !_isLoaded) return;
            _silhouetteSource.Completed += OnDownloadCompleted;
            _silhouetteSource.Failed += OnDownloadFailed;
            _downloadHooked = true;
        }

        private void UnhookDownload()
        {
            if (!_downloadHooked || _silhouetteSource == null) return;
            _silhouetteSource.Completed -= OnDownloadCompleted;
            _silhouetteSource.Failed -= OnDownloadFailed;
            _downloadHooked = false;
        }

        private void OnElementLoaded(object sender, RoutedEventArgs e)
        {
            System.Threading.Interlocked.Increment(ref _loadGeneration);
            _isLoaded = true;
            if (_elementRef.TryGetTarget(out var element))
            {
                element.SetValue(ElementLoadedProperty, true);
            }
            // Re-evaluate on element load or reload (e.g. after virtualization recycling or scrollback)
            Evaluate();
        }

        private void OnElementUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            System.Threading.Interlocked.Increment(ref _loadGeneration);
            if (_elementRef.TryGetTarget(out var element))
            {
                element.SetValue(ElementLoadedProperty, false);
            }
            // Detach download listener while element is off-screen
            UnhookDownload();
        }

        private void OnDownloadCompleted(object? sender, EventArgs e)
        {
            UnhookDownload();
            ScheduleApply(() =>
            {
                if (_silhouetteSource == null) return;
                if (!_elementRef.TryGetTarget(out var element)) return;
                ApplySilhouette(element, _silhouetteSource.PixelWidth, _silhouetteSource.PixelHeight);
            });
        }

        private void OnDownloadFailed(object? sender, EventArgs e)
        {
            UnhookDownload();
            ScheduleApply(() =>
            {
                if (!_elementRef.TryGetTarget(out var element)) return;
                ApplyDefaultSilhouette(element);
            });
        }

        private void ScheduleApply(Action apply)
        {
            // Require the element to still be loaded before accepting or
            // queueing completion/failure work. Recheck inside the dispatcher
            // closure so a callback that raced ahead of Unloaded cannot mutate
            // a virtualized row. The load-generation token also blocks work
            // queued in one loaded generation from applying after reload.
            // Do not read attached properties here: completion can arrive on a
            // worker thread, and GetValue requires dispatcher access.
            if (_disposed) return;
            if (!_isLoaded) return;
            if (!_elementRef.TryGetTarget(out var element)) return;
            if (!_sourceRef.TryGetTarget(out _)) return;

            var generation = _loadGeneration;

            void Invoke()
            {
                if (_disposed) return;
                if (!_isLoaded) return;
                if (generation != _loadGeneration) return;
                if (!_elementRef.TryGetTarget(out var current)) return;
                if (!_sourceRef.TryGetTarget(out var expected)) return;
                if (!ReferenceEquals(GetSource(current), expected)) return;
                apply();
            }

            if (element.Dispatcher.CheckAccess())
            {
                Invoke();
            }
            else
            {
                element.Dispatcher.InvokeAsync(Invoke);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            UnhookDownload();

            if (_elementRef.TryGetTarget(out var element))
            {
                element.Loaded -= OnElementLoaded;
                element.Unloaded -= OnElementUnloaded;
            }
        }
    }
}
