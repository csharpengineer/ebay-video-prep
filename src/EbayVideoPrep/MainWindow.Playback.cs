using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EbayVideoPrep;

public partial class MainWindow
{
    private static readonly double[] PlaybackSpeeds = [0.25, 0.5, 1.0, 2.0, 4.0, 10.0];

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

        SpeedValueText.Text = "1×";
        SpeedSlider.Value = 2;
        _playbackUiTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _playbackUiTimer.Stop();

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

        InitializeTimelineFromMedia();

        // Each newly opened video starts at normal inspection speed.
        SpeedSlider.IsEnabled = true;
        SpeedSlider.Value = 2;
        ApplyPlaybackSpeed();
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
        if (!_mediaReady || _isScrubbing)
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
        if (!_mediaReady || !TimelineSlider.IsEnabled || _exporting)
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
        if (!_isScrubbing || !_mediaReady || _mediaDuration <= TimeSpan.Zero)
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

        if (_resumeAfterScrub && _mediaReady && !_exporting)
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

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValueText is null || VideoPlayer is null)
        {
            return;
        }

        ApplyPlaybackSpeed();
    }

    private void ApplyPlaybackSpeed()
    {
        var index = Math.Clamp(
            (int)Math.Round(SpeedSlider.Value),
            0,
            PlaybackSpeeds.Length - 1);
        var speed = PlaybackSpeeds[index];

        SpeedValueText.Text = FormatPlaybackSpeed(speed);

        if (_mediaReady)
        {
            VideoPlayer.SpeedRatio = speed;
        }
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

    private static string FormatPlaybackSpeed(double speed)
    {
        return speed switch
        {
            0.25 => "0.25×",
            0.5 => "0.5×",
            1.0 => "1×",
            2.0 => "2×",
            4.0 => "4×",
            10.0 => "10×",
            _ => $"{speed:0.##}×"
        };
    }
}
