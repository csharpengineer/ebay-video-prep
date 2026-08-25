using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EbayVideoPrep.Services;

namespace EbayVideoPrep;

public partial class MainWindow
{
    private readonly FastCompositePreviewService _compositePreviewService = new();

    private CancellationTokenSource? _compositeCancellation;
    private bool _compositeInitialized;
    private bool _compositeGenerating;
    private bool _compositeReady;
    private string? _compositeError;

    private bool IsLoopPreviewMode => LoopModeRadio.IsChecked == true;
    private bool ShouldPauseVideoForComposite =>
        CompositeModeRadio.IsChecked == true && _compositeReady;

    private void InitializeCompositeMode()
    {
        if (_compositeInitialized)
        {
            return;
        }

        _compositeInitialized = true;
        VideoPlayer.MediaOpened += Composite_VideoPlayer_MediaOpened;
    }

    private void ShutdownCompositeMode()
    {
        CancelCompositeGeneration();

        if (_compositeInitialized)
        {
            VideoPlayer.MediaOpened -= Composite_VideoPlayer_MediaOpened;
            _compositeInitialized = false;
        }
    }

    private void Composite_VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        ResetCompositeForOpenedVideo();
        StartCompositeGeneration();
    }

    private void ResetCompositeForOpenedVideo()
    {
        CancelCompositeGeneration();

        _compositeGenerating = false;
        _compositeReady = false;
        _compositeError = null;
        CompositeImage.Source = null;
        CompositeImage.Visibility = Visibility.Collapsed;
        CompositeBusyBorder.Visibility = Visibility.Collapsed;

        // Composite is intentionally the default for every newly opened product video.
        CompositeModeRadio.IsChecked = true;
        LoopModeRadio.IsChecked = false;
        TimelinePanel.Visibility = Visibility.Collapsed;
    }

    private void CompositeMode_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaReady && !_compositeReady && !_compositeGenerating)
        {
            StartCompositeGeneration();
        }

        ApplyPreviewMode();
    }

    private void LoopMode_Click(object sender, RoutedEventArgs e)
    {
        ApplyPreviewMode();
    }

    private void ApplyPreviewMode()
    {
        if (!_mediaReady)
        {
            CompositeImage.Visibility = Visibility.Collapsed;
            CompositeBusyBorder.Visibility = Visibility.Collapsed;
            TimelinePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (CompositeModeRadio.IsChecked == true)
        {
            TimelinePanel.Visibility = Visibility.Collapsed;

            if (_compositeReady && CompositeImage.Source is not null)
            {
                CompositeImage.Visibility = Visibility.Visible;
                VideoCanvas.Visibility = Visibility.Collapsed;
                CompositeBusyBorder.Visibility = Visibility.Collapsed;
                VideoPlayer.Pause();
            }
            else
            {
                // Keep the live preview usable while the independent frame seeks run.
                CompositeImage.Visibility = Visibility.Collapsed;
                VideoCanvas.Visibility = Visibility.Visible;
                CompositeBusyBorder.Visibility = _compositeGenerating
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (!_exporting)
                {
                    VideoPlayer.SpeedRatio = 1.0;
                    VideoPlayer.Play();
                }
            }
        }
        else
        {
            CompositeImage.Visibility = Visibility.Collapsed;
            CompositeBusyBorder.Visibility = Visibility.Collapsed;
            VideoCanvas.Visibility = Visibility.Visible;
            TimelinePanel.Visibility = Visibility.Visible;

            if (!_exporting)
            {
                VideoPlayer.SpeedRatio = 1.0;
                VideoPlayer.Play();
            }
        }
    }

    private async void StartCompositeGeneration()
    {
        if (!_mediaReady || _inputPath is null || _compositeGenerating)
        {
            return;
        }

        CancelCompositeGeneration();

        var inputPath = _inputPath;
        var loadGeneration = _loadGeneration;
        var cancellation = new CancellationTokenSource();
        _compositeCancellation = cancellation;
        _compositeGenerating = true;
        _compositeReady = false;
        _compositeError = null;

        var duration = VideoPlayer.NaturalDuration.HasTimeSpan
            ? VideoPlayer.NaturalDuration.TimeSpan
            : TimeSpan.FromSeconds(10);

        CompositeBusyText.Text = "Grabbing turntable snapshots...";
        ApplyPreviewMode();

        if (CompositeModeRadio.IsChecked == true)
        {
            StatusText.Text = "Building composite from parallel fast-seek snapshots...";
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "EbayVideoPrep",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDirectory);

            var result = await _compositePreviewService.CreateAsync(
                inputPath,
                tempDirectory,
                duration,
                cancellation.Token);

            if (cancellation.IsCancellationRequested ||
                loadGeneration != _loadGeneration ||
                !string.Equals(inputPath, _inputPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var bitmap = BuildGhostComposite(result.Frames);
            CompositeImage.Source = bitmap;
            _compositeReady = true;
            _compositeGenerating = false;

            ApplyPreviewMode();

            var partialText = result.SampleCount < result.RequestedSampleCount
                ? $" ({result.SampleCount} of {result.RequestedSampleCount} seeks completed)"
                : string.Empty;

            StatusText.Text =
                $"Composite ready from {result.SampleCount} direct snapshots across " +
                $"{result.SampleSpanSeconds:0.#} seconds{partialText}. " +
                "Adjust the crop to cover the item's full motion, then save.";
        }
        catch (OperationCanceledException)
        {
            // Expected when another file is opened or the application closes.
        }
        catch (Exception ex)
        {
            if (loadGeneration != _loadGeneration)
            {
                return;
            }

            _compositeGenerating = false;
            _compositeReady = false;
            _compositeError = ex.Message;

            // If fewer than two fast seeks succeed, the live loop is more useful than a
            // single still frame pretending to be a motion composite.
            CompositeModeRadio.IsChecked = false;
            LoopModeRadio.IsChecked = true;
            ApplyPreviewMode();
            StatusText.Text = $"Fast composite unavailable; showing Loop instead. {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_compositeCancellation, cancellation))
            {
                _compositeCancellation = null;
            }

            cancellation.Dispose();

            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch
            {
                // Temporary thumbnail cleanup is best effort only. BitmapCacheOption.OnLoad
                // means successful images no longer hold the files open at this point.
            }
        }
    }

    private void CancelCompositeGeneration()
    {
        if (_compositeCancellation is null)
        {
            return;
        }

        try
        {
            _compositeCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A just-finished generation may already have disposed its token source.
        }

        _compositeCancellation = null;
    }

    private static BitmapSource BuildGhostComposite(IReadOnlyList<FastCompositeFrame> frames)
    {
        var bitmaps = frames
            .Select(frame => LoadBitmap(frame.Path))
            .ToArray();

        if (bitmaps.Length == 0)
        {
            throw new InvalidOperationException("No extracted preview frames were available to compose.");
        }

        var width = bitmaps[0].PixelWidth;
        var height = bitmaps[0].PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The extracted preview frame had invalid dimensions.");
        }

        var bounds = new Rect(0, 0, width, height);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var drawing = visual.RenderOpen())
        {
            // Keep the earliest view recognizable, then layer alternate turntable angles
            // lightly over it. Sequential alpha blending gives the anchor roughly 60% of
            // the final visual weight with all four frames present.
            drawing.DrawImage(bitmaps[0], bounds);

            double[] ghostOpacities = [0.18, 0.15, 0.12];
            for (var index = 1; index < bitmaps.Length; index++)
            {
                var opacity = ghostOpacities[Math.Min(index - 1, ghostOpacities.Length - 1)];
                drawing.PushOpacity(opacity);
                drawing.DrawImage(bitmaps[index], bounds);
                drawing.Pop();
            }
        }

        var render = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        render.Render(visual);
        render.Freeze();
        return render;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames[0];
        bitmap.Freeze();
        return bitmap;
    }
}
