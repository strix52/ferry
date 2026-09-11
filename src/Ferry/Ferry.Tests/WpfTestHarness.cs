using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ferry;

namespace Ferry.Tests;

internal static class WpfTestHarness
{
    public static void RunInSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ctx = new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
                SynchronizationContext.SetSynchronizationContext(ctx);
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    public static void PumpDispatcher()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { frame.Continue = false; });
        Dispatcher.PushFrame(frame);
    }

    public static void InvokeFromWorker(Action action)
    {
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        thread.IsBackground = true;
        thread.Start();
        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Worker thread did not finish callback within 5s");
        }
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    public static BitmapSource CreateBitmap(int width, int height)
    {
        var dpi = 96.0;
        var pixelFormat = PixelFormats.Pbgra32;
        var stride = width * 4;
        var pixels = new byte[height * stride];
        return BitmapSource.Create(width, height, dpi, dpi, pixelFormat, null, pixels, stride);
    }

    public sealed class TestSilhouetteSource : ThumbnailSilhouette.ISilhouetteSource
    {
        public bool IsDownloading { get; set; }
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        public int CompletedHookCount { get; private set; }
        public int FailedHookCount { get; private set; }

        private EventHandler? _completed;
        private EventHandler? _failed;

        public event EventHandler? Completed
        {
            add { _completed += value; CompletedHookCount++; }
            remove { _completed -= value; CompletedHookCount--; }
        }

        public event EventHandler? Failed
        {
            add { _failed += value; FailedHookCount++; }
            remove { _failed -= value; FailedHookCount--; }
        }

        public void SimulateComplete(int width, int height)
        {
            IsDownloading = false;
            PixelWidth = width;
            PixelHeight = height;
            _completed?.Invoke(this, EventArgs.Empty);
        }

        public void SimulateFail()
        {
            IsDownloading = false;
            _failed?.Invoke(this, EventArgs.Empty);
        }
    }
}
