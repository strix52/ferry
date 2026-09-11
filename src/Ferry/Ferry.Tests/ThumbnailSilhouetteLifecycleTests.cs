using System.Windows;
using System.Windows.Controls;
using Ferry;
using Xunit;

namespace Ferry.Tests;

public class ThumbnailSilhouetteLifecycleTests
{
    [Fact]
    public void PortraitBitmap_SnapsTo180()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var portraitBmp = WpfTestHarness.CreateBitmap(1080, 2400);
            ThumbnailSilhouette.SetSource(element, portraitBmp);
            Assert.Equal(180.0, element.MaxWidth);
            Assert.Equal(180.0, element.Width);
        });
    }

    [Fact]
    public void SquareBitmap_SnapsTo220()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var squareBmp = WpfTestHarness.CreateBitmap(1000, 1000);
            ThumbnailSilhouette.SetSource(element, squareBmp);
            Assert.Equal(220.0, element.MaxWidth);
            Assert.Equal(220.0, element.Width);
        });
    }

    [Fact]
    public void LandscapeBitmap_SnapsTo380_ResponsiveNaNWidth()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var landscapeBmp = WpfTestHarness.CreateBitmap(1920, 1080);
            ThumbnailSilhouette.SetSource(element, landscapeBmp);
            Assert.Equal(380.0, element.MaxWidth);
            Assert.True(double.IsNaN(element.Width));
        });
    }

    [Fact]
    public void SourceAssignedWhileUnloaded_RegistersZeroSubscriptions()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var portraitBmp = WpfTestHarness.CreateBitmap(1080, 2400);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var manager = ThumbnailSilhouette.CreateManagerForTesting(element, portraitBmp, mockSource);
            Assert.Equal(0, mockSource.CompletedHookCount);
            Assert.Equal(0, mockSource.FailedHookCount);
            Assert.False(manager.IsHooked);
            Assert.False(manager.IsLoadedState);
            Assert.Equal(380.0, element.MaxWidth);
        });
    }

    [Fact]
    public void SubsequentLoadWhileDownloading_RegistersSubscriptions()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var portraitBmp = WpfTestHarness.CreateBitmap(1080, 2400);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var manager = ThumbnailSilhouette.CreateManagerForTesting(element, portraitBmp, mockSource);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(1, mockSource.CompletedHookCount);
            Assert.Equal(1, mockSource.FailedHookCount);
            Assert.True(manager.IsHooked);
            Assert.True(manager.IsLoadedState);
        });
    }

    [Fact]
    public void RepeatedEvaluation_SubscriptionsRemainOne()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var portraitBmp = WpfTestHarness.CreateBitmap(1080, 2400);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var manager = ThumbnailSilhouette.CreateManagerForTesting(element, portraitBmp, mockSource);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            manager.Evaluate();
            manager.Evaluate();
            Assert.Equal(1, mockSource.CompletedHookCount);
            Assert.Equal(1, mockSource.FailedHookCount);
        });
    }

    [Fact]
    public void Unload_SubscriptionsReturnToZero()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var portraitBmp = WpfTestHarness.CreateBitmap(1080, 2400);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var manager = ThumbnailSilhouette.CreateManagerForTesting(element, portraitBmp, mockSource);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.Equal(0, mockSource.CompletedHookCount);
            Assert.Equal(0, mockSource.FailedHookCount);
            Assert.False(manager.IsHooked);
        });
    }

    [Fact]
    public void CompletionWhileUnloaded_EvaluatedOnReload()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var portraitBmp = WpfTestHarness.CreateBitmap(1080, 2400);
            var mockSource = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            ThumbnailSilhouette.CreateManagerForTesting(element, portraitBmp, mockSource);
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

            mockSource.SimulateComplete(1080, 2400);
            Assert.Equal(0, mockSource.CompletedHookCount);
            Assert.Equal(380.0, element.MaxWidth);

            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(180.0, element.MaxWidth);
            Assert.Equal(180.0, element.Width);
        });
    }

    [Fact]
    public void ContainerRecycling_UnhooksOldSourceAndHooksNewSource()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            ThumbnailSilhouette.SetLoadedForTesting(element, true);
            var mockSourceOld = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var dummyBmpOld = WpfTestHarness.CreateBitmap(100, 100);
            var dummyBmpNew = WpfTestHarness.CreateBitmap(200, 200);

            ThumbnailSilhouette.CreateManagerForTesting(element, dummyBmpOld, mockSourceOld);
            Assert.Equal(1, mockSourceOld.CompletedHookCount);
            Assert.Equal(1, mockSourceOld.FailedHookCount);

            var mockSourceNew = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            ThumbnailSilhouette.CreateManagerForTesting(element, dummyBmpNew, mockSourceNew);
            Assert.Equal(0, mockSourceOld.CompletedHookCount);
            Assert.Equal(0, mockSourceOld.FailedHookCount);
            Assert.Equal(1, mockSourceNew.CompletedHookCount);
            Assert.Equal(1, mockSourceNew.FailedHookCount);

            mockSourceOld.SimulateComplete(1080, 2400);
            mockSourceNew.SimulateComplete(1920, 1080);
            Assert.Equal(380.0, element.MaxWidth);
            Assert.True(double.IsNaN(element.Width));
        });
    }

    [Fact]
    public void FailureWhileLoaded_ResetsToDefaultSilhouette()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            ThumbnailSilhouette.SetLoadedForTesting(element, true);
            var mockSourceFail = new WpfTestHarness.TestSilhouetteSource { IsDownloading = true };
            var dummyBmp = WpfTestHarness.CreateBitmap(100, 100);

            ThumbnailSilhouette.CreateManagerForTesting(element, dummyBmp, mockSourceFail);
            Assert.Equal(1, mockSourceFail.CompletedHookCount);
            mockSourceFail.SimulateFail();
            Assert.Equal(0, mockSourceFail.CompletedHookCount);
            Assert.Equal(0, mockSourceFail.FailedHookCount);
            Assert.Equal(380.0, element.MaxWidth);
        });
    }

    [Fact]
    public void NullSource_ResetsToDefault()
    {
        WpfTestHarness.RunInSta(() =>
        {
            var element = new Border();
            var bmp = WpfTestHarness.CreateBitmap(100, 100);
            ThumbnailSilhouette.SetSource(element, bmp);
            ThumbnailSilhouette.SetSource(element, null);
            Assert.Equal(380.0, element.MaxWidth);
            Assert.Equal(220.0, element.MaxHeight);
        });
    }
}
