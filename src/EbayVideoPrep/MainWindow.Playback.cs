using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EbayVideoPrep;

public partial class MainWindow
{
    private readonly DispatcherTimer _playbackUiTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };

    private bool _playbackControlsInitialized;
    private bool _isScrubbing;
    private bool _resumeAfterScrub;
    private TimeSpan _mediaDuration = TimeSpan.Zero;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_playbackControlsInitialized)
        {
            return;
        }

        _playbackControlsInitialized = true;
        _playbackUiTimer.Tick += PlaybackUiTimer_Tick;
        VideoPlayer.MediaOpened += Playback_VideoPlayer_MediaOpened;
        VideoPlayer.MediaEnded += Playback_VideoPlayer_MediaEnded;

        InitializeCompositeMode();
        _playbackUiTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _playbackUiTimer.Stop();
        ShutdownCompositeMode();

        if (_playbackControlsInitialized)
        {
            VideoPlayer.MediaOpened -= Playback_VideoPlayer_MediaOpened;
            VideoPlayer.MediaEnded -= Playback_VideoPlayer_MediaEnded;
        }
    }

    private void Playback_VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _isScrubbing = false;
        _resumeAfterScrub = false;
        VideoPlayer.SpeedRatio = 1.0;
        InitializeTimelineFromMedia();
    }

    private void Playback_VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_isScrubbing)
        {
            return;
        }

        TimelineSlider.Value = 0;
        UpdateTimeReadout(TimeSpan.Zero);
    }

    private void PlaybackUiTimer_Tick(object? sender, EventArgs e)
    {
        // Composite is a static inspection view. Keep the hidden MediaElement paused so
        // it does not consume CPU after an export or other code path restarts playback.
        if (ShouldPauseVideoForComposite)
        {
            VideoPlayer.Pause();
            return;
        }

        if (!_mediaReady || _isScrubbing || !IsLoopPreviewMode)
        {
            return;
        }

        EnsureTimelineDuration();

        if (_mediaDuration <= TimeSpan.Zero)
        {
            return;
        }

        var position = VideoPlayer.Position;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }
        else if (position > _mediaDuration)
        {
            position = _mediaDuration;
        }

        TimelineSlider.Value = position.TotalSeconds;
        UpdateTimeReadout(position);
    }

    private void InitializeTimelineFromMedia()
    {
        _mediaDuration = TimeSpan.Zero;
        TimelineSlider.Minimum = 0;
        TimelineSlider.Maximum = 1;
        TimelineSlider.Value = 0;
        TimelineSlider.IsEnabled = false;
        UpdateTimeReadout(TimeSpan.Zero);

        EnsureTimelineDuration();
    }

    private void EnsureTimelineDuration()
    {
        if (_mediaDuration > TimeSpan.Zero || !VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var duration = VideoPlayer.NaturalDuration.TimeSpan;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _mediaDuration = duration;
        TimelineSlider.Maximum = Math.Max(0.001, duration.TotalSeconds);
        TimelineSlider.IsEnabled = true;
        UpdateTimeReadout(VideoPlayer.Position);
    }

    private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mediaReady || !TimelineSlider.IsEnabled || _exporting || !IsLoopPreviewMode)
        {
            return;
        }

        _isScrubbing = true;
        _resumeAfterScrub = true;
        VideoPlayer.Pause();
    }

    private void TimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FinishTimelineScrub();
    }

    private void TimelineSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        FinishTimelineScrub();
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isScrubbing || !_mediaReady || _mediaDuration <= TimeSpan.Zero || !IsLoopPreviewMode)
        {
            return;
        }

        SeekToTimelineValue();
    }

    private void FinishTimelineScrub()
    {
        if (!_isScrubbing)
        {
            return;
        }

        SeekToTimelineValue();
        _isScrubbing = false;

        if (_resumeAfterScrub && _mediaReady && !_exporting && IsLoopPreviewMode)
        {
            VideoPlayer.Play();
        }

        _resumeAfterScrub = false;
    }

    private void SeekToTimelineValue()
    {
        if (_mediaDuration <= TimeSpan.Zero)
        {
            return;
        }

        var seconds = Math.Clamp(
            TimelineSlider.Value,
            0,
            _mediaDuration.TotalSeconds);
        var position = TimeSpan.FromSeconds(seconds);

        VideoPlayer.Position = position;
        UpdateTimeReadout(position);
    }

    private void UpdateTimeReadout(TimeSpan position)
    {
        var duration = _mediaDuration > TimeSpan.Zero
            ? _mediaDuration
            : TimeSpan.Zero;

        TimeReadoutText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}
