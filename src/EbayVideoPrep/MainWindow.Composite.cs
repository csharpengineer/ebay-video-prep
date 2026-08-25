using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace EbayVideoPrep;

public partial class MainWindow
{
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
                // Keep the live preview visible while the composite is being built so
                // opening a video never leaves the user staring at an empty frame.
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
            : TimeSpan.FromMinutes(2);

        CompositeBusyText.Text = duration > TimeSpan.FromMinutes(2)
            ? "Building composite from the first 2 minutes..."
            : "Building composite from 1-second samples...";

        ApplyPreviewMode();

        if (CompositeModeRadio.IsChecked == true)
        {
            StatusText.Text = "Building composite view from one frame per second...";
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "EbayVideoPrep",
            Guid.NewGuid().ToString("N"));
        var compositePath = Path.Combine(tempDirectory, "composite.png");

        try
        {
            Directory.CreateDirectory(tempDirectory);

            var result = await _ffmpegService.CreateCompositePreviewAsync(
                inputPath,
                compositePath,
                duration,
                cancellation.Token);

            if (cancellation.IsCancellationRequested ||
                loadGeneration != _loadGeneration ||
                !string.Equals(inputPath, _inputPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var bitmap = LoadBitmap(compositePath);
            CompositeImage.Source = bitmap;
            _compositeReady = true;
            _compositeGenerating = false;

            ApplyPreviewMode();

            StatusText.Text =
                $"Composite ready from {result.SampleCount} one-second samples. " +
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

            // Fall back to the loop view rather than leaving a broken default mode selected.
            CompositeModeRadio.IsChecked = false;
            LoopModeRadio.IsChecked = true;
            ApplyPreviewMode();
            StatusText.Text = $"Composite view could not be built; showing Loop instead. {ex.Message}";
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
                // Temporary preview cleanup is best effort only.
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
