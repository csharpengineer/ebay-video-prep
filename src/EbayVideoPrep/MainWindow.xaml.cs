using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using EbayVideoPrep.Models;
using EbayVideoPrep.Services;
using Microsoft.Win32;

namespace EbayVideoPrep;

public partial class MainWindow : Window
{
    private const double MinimumCropDisplaySize = 40.0;

    private readonly FfmpegService _ffmpegService = new();

    private string? _inputPath;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _mediaReady;
    private bool _exporting;

    // Stored as fractions of the source frame so the crop remains stable as the window resizes.
    private Rect _normalizedCrop = new(0, 0, 1, 1);
    private Rect _videoDisplayRect = Rect.Empty;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open product video",
            Filter = "Video files (*.mp4;*.mov)|*.mp4;*.mov|MP4 files (*.mp4)|*.mp4|MOV files (*.mov)|*.mov|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            LoadVideo(dialog.FileName);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetFirstSupportedDroppedFile(e.Data) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var path = GetFirstSupportedDroppedFile(e.Data);
        if (path is null)
        {
            StatusText.Text = "Drop an MP4 or MOV video file.";
            return;
        }

        LoadVideo(path);
    }

    private static string? GetFirstSupportedDroppedFile(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return null;
        }

        return files.FirstOrDefault(IsSupportedVideoFile);
    }

    private static bool IsSupportedVideoFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    private void LoadVideo(string path)
    {
        if (_exporting)
        {
            return;
        }

        _inputPath = Path.GetFullPath(path);
        _mediaReady = false;
        _sourceWidth = 0;
        _sourceHeight = 0;
        _normalizedCrop = new Rect(0, 0, 1, 1);

        SaveButton.IsEnabled = false;
        CropCanvas.Visibility = Visibility.Collapsed;
        DropHint.Visibility = Visibility.Collapsed;
        CropInfoText.Text = "Loading...";
        StatusText.Text = $"Loading {Path.GetFileName(_inputPath)}...";

        VideoPlayer.Stop();
        VideoPlayer.Source = new Uri(_inputPath, UriKind.Absolute);
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _sourceWidth = VideoPlayer.NaturalVideoWidth;
        _sourceHeight = VideoPlayer.NaturalVideoHeight;

        if (_sourceWidth <= 0 || _sourceHeight <= 0)
        {
            HandleMediaError("Windows could not determine the video's frame dimensions.");
            return;
        }

        _mediaReady = true;
        _normalizedCrop = new Rect(0, 0, 1, 1);

        RecalculateVideoDisplayRect();
        CropCanvas.Visibility = Visibility.Visible;
        UpdateCropVisuals();

        SaveButton.IsEnabled = true;
        StatusText.Text = $"{Path.GetFileName(_inputPath)} — {_sourceWidth}×{_sourceHeight}. Drag the crop box, then save.";

        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady)
        {
            return;
        }

        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        HandleMediaError(e.ErrorException?.Message ?? "Windows could not play this video.");
    }

    private void HandleMediaError(string message)
    {
        _mediaReady = false;
        SaveButton.IsEnabled = false;
        CropCanvas.Visibility = Visibility.Collapsed;
        DropHint.Visibility = Visibility.Visible;
        CropInfoText.Text = "Video could not be opened";
        StatusText.Text = message;

        MessageBox.Show(
            this,
            $"The video could not be opened for preview.\n\n{message}\n\n" +
            "This MVP uses Windows' built-in media playback for the preview. FFmpeg may still support formats that Windows does not.",
            "Unable to open video",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_mediaReady)
        {
            return;
        }

        RecalculateVideoDisplayRect();
        UpdateCropVisuals();
    }

    private void RecalculateVideoDisplayRect()
    {
        if (!_mediaReady ||
            _sourceWidth <= 0 ||
            _sourceHeight <= 0 ||
            PreviewHost.ActualWidth <= 0 ||
            PreviewHost.ActualHeight <= 0)
        {
            _videoDisplayRect = Rect.Empty;
            return;
        }

        var hostWidth = PreviewHost.ActualWidth;
        var hostHeight = PreviewHost.ActualHeight;
        var sourceAspect = (double)_sourceWidth / _sourceHeight;
        var hostAspect = hostWidth / hostHeight;

        if (hostAspect > sourceAspect)
        {
            var height = hostHeight;
            var width = height * sourceAspect;
            _videoDisplayRect = new Rect((hostWidth - width) / 2.0, 0, width, height);
        }
        else
        {
            var width = hostWidth;
            var height = width / sourceAspect;
            _videoDisplayRect = new Rect(0, (hostHeight - height) / 2.0, width, height);
        }
    }

    private void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady)
        {
            return;
        }

        _normalizedCrop = new Rect(0, 0, 1, 1);
        UpdateCropVisuals();
    }

    private void SquareCrop_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady)
        {
            return;
        }

        var size = Math.Min(_sourceWidth, _sourceHeight);
        var x = (_sourceWidth - size) / 2.0 / _sourceWidth;
        var y = (_sourceHeight - size) / 2.0 / _sourceHeight;
        var width = (double)size / _sourceWidth;
        var height = (double)size / _sourceHeight;

        _normalizedCrop = new Rect(x, y, width, height);
        UpdateCropVisuals();
    }

    private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_mediaReady || _videoDisplayRect.IsEmpty)
        {
            return;
        }

        var crop = GetVisualCropRect();
        var left = Clamp(
            crop.Left + e.HorizontalChange,
            _videoDisplayRect.Left,
            _videoDisplayRect.Right - crop.Width);
        var top = Clamp(
            crop.Top + e.VerticalChange,
            _videoDisplayRect.Top,
            _videoDisplayRect.Bottom - crop.Height);

        UpdateNormalizedCropFromVisual(new Rect(left, top, crop.Width, crop.Height));
        UpdateCropVisuals();
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_mediaReady ||
            _videoDisplayRect.IsEmpty ||
            sender is not Thumb thumb ||
            thumb.Tag is not string handle)
        {
            return;
        }

        var crop = GetVisualCropRect();
        var left = crop.Left;
        var top = crop.Top;
        var right = crop.Right;
        var bottom = crop.Bottom;
        var minSize = Math.Min(
            MinimumCropDisplaySize,
            Math.Min(_videoDisplayRect.Width, _videoDisplayRect.Height));

        if (handle.Contains("Left", StringComparison.Ordinal))
        {
            left = Clamp(left + e.HorizontalChange, _videoDisplayRect.Left, right - minSize);
        }

        if (handle.Contains("Right", StringComparison.Ordinal))
        {
            right = Clamp(right + e.HorizontalChange, left + minSize, _videoDisplayRect.Right);
        }

        if (handle.Contains("Top", StringComparison.Ordinal))
        {
            top = Clamp(top + e.VerticalChange, _videoDisplayRect.Top, bottom - minSize);
        }

        if (handle.Contains("Bottom", StringComparison.Ordinal))
        {
            bottom = Clamp(bottom + e.VerticalChange, top + minSize, _videoDisplayRect.Bottom);
        }

        UpdateNormalizedCropFromVisual(new Rect(left, top, right - left, bottom - top));
        UpdateCropVisuals();
    }

    private Rect GetVisualCropRect()
    {
        if (_videoDisplayRect.IsEmpty)
        {
            return Rect.Empty;
        }

        return new Rect(
            _videoDisplayRect.Left + (_normalizedCrop.X * _videoDisplayRect.Width),
            _videoDisplayRect.Top + (_normalizedCrop.Y * _videoDisplayRect.Height),
            _normalizedCrop.Width * _videoDisplayRect.Width,
            _normalizedCrop.Height * _videoDisplayRect.Height);
    }

    private void UpdateNormalizedCropFromVisual(Rect visualCrop)
    {
        if (_videoDisplayRect.IsEmpty)
        {
            return;
        }

        var x = (visualCrop.Left - _videoDisplayRect.Left) / _videoDisplayRect.Width;
        var y = (visualCrop.Top - _videoDisplayRect.Top) / _videoDisplayRect.Height;
        var width = visualCrop.Width / _videoDisplayRect.Width;
        var height = visualCrop.Height / _videoDisplayRect.Height;

        x = Clamp(x, 0, 1);
        y = Clamp(y, 0, 1);
        width = Clamp(width, 0, 1 - x);
        height = Clamp(height, 0, 1 - y);

        _normalizedCrop = new Rect(x, y, width, height);
    }

    private void UpdateCropVisuals()
    {
        if (!_mediaReady || _videoDisplayRect.IsEmpty)
        {
            return;
        }

        var crop = GetVisualCropRect();

        SetElementRect(CropBorder, crop);
        SetElementRect(MoveThumb, crop);

        SetElementRect(
            ShadeTop,
            new Rect(
                _videoDisplayRect.Left,
                _videoDisplayRect.Top,
                _videoDisplayRect.Width,
                Math.Max(0, crop.Top - _videoDisplayRect.Top)));

        SetElementRect(
            ShadeBottom,
            new Rect(
                _videoDisplayRect.Left,
                crop.Bottom,
                _videoDisplayRect.Width,
                Math.Max(0, _videoDisplayRect.Bottom - crop.Bottom)));

        SetElementRect(
            ShadeLeft,
            new Rect(
                _videoDisplayRect.Left,
                crop.Top,
                Math.Max(0, crop.Left - _videoDisplayRect.Left),
                crop.Height));

        SetElementRect(
            ShadeRight,
            new Rect(
                crop.Right,
                crop.Top,
                Math.Max(0, _videoDisplayRect.Right - crop.Right),
                crop.Height));

        PositionResizeHandles(crop);
        UpdateCropReadout();
    }

    private void PositionResizeHandles(Rect crop)
    {
        foreach (var thumb in CropCanvas.Children.OfType<Thumb>())
        {
            if (ReferenceEquals(thumb, MoveThumb) || thumb.Tag is not string handle)
            {
                continue;
            }

            double x;
            double y;

            switch (handle)
            {
                case "TopLeft":
                    x = crop.Left;
                    y = crop.Top;
                    break;
                case "Top":
                    x = crop.Left + crop.Width / 2.0;
                    y = crop.Top;
                    break;
                case "TopRight":
                    x = crop.Right;
                    y = crop.Top;
                    break;
                case "Right":
                    x = crop.Right;
                    y = crop.Top + crop.Height / 2.0;
                    break;
                case "BottomRight":
                    x = crop.Right;
                    y = crop.Bottom;
                    break;
                case "Bottom":
                    x = crop.Left + crop.Width / 2.0;
                    y = crop.Bottom;
                    break;
                case "BottomLeft":
                    x = crop.Left;
                    y = crop.Bottom;
                    break;
                case "Left":
                    x = crop.Left;
                    y = crop.Top + crop.Height / 2.0;
                    break;
                default:
                    continue;
            }

            Canvas.SetLeft(thumb, x - thumb.Width / 2.0);
            Canvas.SetTop(thumb, y - thumb.Height / 2.0);
        }
    }

    private void UpdateCropReadout()
    {
        var crop = GetSourceCropRegion();
        CropInfoText.Text = $"Crop {crop.Width}×{crop.Height} at {crop.X},{crop.Y}";
    }

    private CropRegion GetSourceCropRegion()
    {
        var x = MakeEven((int)Math.Round(_normalizedCrop.X * _sourceWidth));
        var y = MakeEven((int)Math.Round(_normalizedCrop.Y * _sourceHeight));

        x = Math.Clamp(x, 0, Math.Max(0, MakeEven(_sourceWidth - 2)));
        y = Math.Clamp(y, 0, Math.Max(0, MakeEven(_sourceHeight - 2)));

        var width = MakeEven((int)Math.Round(_normalizedCrop.Width * _sourceWidth));
        var height = MakeEven((int)Math.Round(_normalizedCrop.Height * _sourceHeight));

        var maxWidth = Math.Max(2, MakeEven(_sourceWidth - x));
        var maxHeight = Math.Max(2, MakeEven(_sourceHeight - y));

        width = Math.Clamp(width, 2, maxWidth);
        height = Math.Clamp(height, 2, maxHeight);

        return new CropRegion(x, y, width, height);
    }

    private async void SaveVideo_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady || _inputPath is null || _exporting)
        {
            return;
        }

        var sourceName = Path.GetFileNameWithoutExtension(_inputPath);
        var dialog = new SaveFileDialog
        {
            Title = "Save eBay-ready video",
            Filter = "MP4 video (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = sourceName + "-ebay.mp4",
            InitialDirectory = Path.GetDirectoryName(_inputPath),
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (Path.GetFullPath(dialog.FileName).Equals(
                Path.GetFullPath(_inputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                "Choose a different filename so the original recording is preserved.",
                "Keep the original video",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var crop = GetSourceCropRegion();

        try
        {
            _exporting = true;
            SaveButton.IsEnabled = false;
            ExportProgress.Visibility = Visibility.Visible;
            StatusText.Text = $"Exporting {Path.GetFileName(dialog.FileName)}...";
            VideoPlayer.Pause();

            var result = await _ffmpegService.ExportAsync(
                _inputPath,
                dialog.FileName,
                crop);

            var sizeMb = result.FileSizeBytes / 1024.0 / 1024.0;
            StatusText.Text = $"Saved {Path.GetFileName(dialog.FileName)} — {sizeMb:N1} MB.";

            if (result.ExceedsEbayFileSizeLimit)
            {
                MessageBox.Show(
                    this,
                    $"The video was saved successfully, but it is {sizeMb:N1} MB.\n\n" +
                    "eBay currently limits listing-video uploads to 150 MB. A future pass can add automatic bitrate targeting for oversized files.",
                    "Saved, but over eBay's size limit",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    this,
                    $"Saved an eBay-ready MP4.\n\n{dialog.FileName}\n\nSize: {sizeMb:N1} MB\nAudio: removed",
                    "Video saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed.";
            MessageBox.Show(
                this,
                ex.Message,
                "Unable to export video",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _exporting = false;
            ExportProgress.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = _mediaReady;

            if (_mediaReady)
            {
                VideoPlayer.Play();
            }
        }
    }

    private static void SetElementRect(FrameworkElement element, Rect rect)
    {
        Canvas.SetLeft(element, rect.Left);
        Canvas.SetTop(element, rect.Top);
        element.Width = Math.Max(0, rect.Width);
        element.Height = Math.Max(0, rect.Height);
    }

    private static int MakeEven(int value)
    {
        return Math.Max(0, value & ~1);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Max(min, Math.Min(max, value));
    }
}
