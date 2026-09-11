using System.Windows;
using System.Windows.Controls;
using Ferry;
using Xunit;

namespace Ferry.Tests;

public class ThumbnailSilhouetteRaceTests
{
    [Fact]
    public void PositiveControl_WorkerThreadCompletion_AppliesWhileLoaded()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            ThumbnailSilhouette.SetLoadedForTesting(element, true);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var bmp = WpfTestHarness.CreateBitmap(100, 100);
            ThumbnailSilhouette.CreateManagerForTesting(element, bmp, mockSource);

            Assert.Equal(1, mockSource.CompletedHookCount);
            Assert.Equal(1, mockSource.FailedHookCount);

            WpfTestHarness.InvokeFromWorker(() => mockSource.SimulateComplete(1080, 2400));
            WpfTestHarness.PumpDispatcher();

            Assert.Equal(180.0, element.MaxWidth);
            Assert.Equal(180.0, element.Width);
        });
    }

    [Fact]
    public void CompletionRace_UnloadBeforeDispatch_QueuedWorkDoesNotMutate_ReloadAppliesDecodedSize()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            ThumbnailSilhouette.SetLoadedForTesting(element, true);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var bmp = WpfTestHarness.CreateBitmap(100, 100);
            var manager = ThumbnailSilhouette.CreateManagerForTesting(element, bmp, mockSource);

            Assert.Equal(1, mockSource.CompletedHookCount);
            Assert.Equal(1, mockSource.FailedHookCount);
            Assert.Equal(380.0, element.MaxWidth);

            // Worker begins callback and queues UI work
            WpfTestHarness.InvokeFromWorker(() => mockSource.SimulateComplete(1080, 2400));

            // Element unloads before the dispatcher runs that work
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, mockSource.CompletedHookCount);
            Assert.Equal(0, mockSource.FailedHookCount);
            Assert.False(manager.IsLoadedState);

            // Pump the dispatcher so the queued closure executes
            WpfTestHarness.PumpDispatcher();

            // The queued apply must NOT have modified the unloaded element
            Assert.Equal(380.0, element.MaxWidth);
            Assert.True(double.IsNaN(element.Width));

            // Reload evaluates completed source and applies decoded size
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(180.0, element.MaxWidth);
            Assert.Equal(180.0, element.Width);
        });
    }

    [Fact]
    public void FailureRace_UnloadBeforeDispatch_QueuedWorkDoesNotMutate_ReloadRestoresDefault()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            ThumbnailSilhouette.SetLoadedForTesting(element, true);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var bmp = WpfTestHarness.CreateBitmap(100, 100);
            var manager = ThumbnailSilhouette.CreateManagerForTesting(element, bmp, mockSource);

            Assert.Equal(1, mockSource.CompletedHookCount);
            element.MaxWidth = 123.0;
            element.Width = 123.0;

            // Worker begins callback and queues fail work
            WpfTestHarness.InvokeFromWorker(() => mockSource.SimulateFail());

            // Element unloads before the dispatcher runs that work
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.False(manager.IsLoadedState);

            // Pump the dispatcher
            WpfTestHarness.PumpDispatcher();

            // The queued apply must NOT have reset dimensions on the unloaded element
            Assert.Equal(123.0, element.MaxWidth);
            Assert.Equal(123.0, element.Width);

            // Reload restores default silhouette
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(380.0, element.MaxWidth);
            Assert.Equal(220.0, element.MaxHeight);
        });
    }

    [Fact]
    public void SourceReplacement_StaleQueuedCallbackCannotMutateRecycledRow()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            ThumbnailSilhouette.SetLoadedForTesting(element, true);
            var mockOld = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var bmpOld = WpfTestHarness.CreateBitmap(110, 110);
            var bmpNew = WpfTestHarness.CreateBitmap(220, 220);

            ThumbnailSilhouette.CreateManagerForTesting(element, bmpOld, mockOld);

            // Old source queues completion
            WpfTestHarness.InvokeFromWorker(() => mockOld.SimulateComplete(1080, 2400));

            // Row is recycled to new source before dispatcher runs
            var mockNew = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            ThumbnailSilhouette.CreateManagerForTesting(element, bmpNew, mockNew);

            // Pump dispatcher to execute old queued completion
            WpfTestHarness.PumpDispatcher();

            // Stale queued completion did not alter the element
            Assert.Equal(380.0, element.MaxWidth);
            Assert.Equal(1, mockNew.CompletedHookCount);
            Assert.Equal(1, mockNew.FailedHookCount);
            Assert.Equal(0, mockOld.CompletedHookCount);
            Assert.Equal(0, mockOld.FailedHookCount);
        });
    }
}
