using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace EbayVideoPrep.Services;

/// <summary>
/// Builds a quick turntable crop aid by seeking directly to a handful of timestamps.
/// Each frame is extracted by a separate FFmpeg process so the app does not have to
/// decode a contiguous section of high-resolution phone video just to make a preview.
/// </summary>
public sealed class FastCompositePreviewService
{
    private const double MaxSampleSpanSeconds = 10.0;
    private const double StartOffsetSeconds = 0.25;
    private const int PreviewMaxDimension = 720;
    private const int DesiredSampleCount = 4;
    private static readonly TimeSpan ExtractionBudget = TimeSpan.FromSeconds(6);

    public async Task<FastCompositePreviewResult> CreateAsync(
        string inputPath,
        string outputDirectory,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var sampleTimes = BuildSampleTimes(duration);
        using var budgetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCancellation.CancelAfter(ExtractionBudget);

        // These are intentionally independent input-side seeks. Running them together is
        // much faster for phone footage than decoding the first ten seconds in sequence.
        var attempts = sampleTimes
            .Select((timestamp, index) => ExtractFrameSafelyAsync(
                inputPath,
                Path.Combine(outputDirectory, $"frame-{index + 1:00}.jpg"),
                timestamp,
                budgetCancellation.Token,
                cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(attempts);
        cancellationToken.ThrowIfCancellationRequested();

        var frames = results
            .Where(result => result.Frame is not null)
            .Select(result => result.Frame!.Value)
            .OrderBy(frame => frame.TimestampSeconds)
            .ToArray();

        // Two distinct views are enough to provide some motion-envelope information.
        // Keep partial results rather than throwing away three good frames because a
        // fourth seek happened to be slow on a particular codec.
        if (frames.Length < 2)
        {
            var diagnostic = results
                .Select(result => result.Error)
                .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));

            throw new InvalidOperationException(
                "Fast composite could not extract at least two preview frames within " +
                $"{ExtractionBudget.TotalSeconds:0} seconds." +
                (diagnostic is null ? string.Empty : $"\n\n{diagnostic}"));
        }

        var spanSeconds = frames[^1].TimestampSeconds - frames[0].TimestampSeconds;
        return new FastCompositePreviewResult(
            frames,
            sampleTimes.Length,
            Math.Max(0, spanSeconds));
    }

    private static double[] BuildSampleTimes(TimeSpan duration)
    {
        var knownDuration = duration > TimeSpan.Zero
            ? duration.TotalSeconds
            : MaxSampleSpanSeconds + StartOffsetSeconds;

        if (knownDuration <= 0.25)
        {
            return [0.0, Math.Max(0.05, knownDuration * 0.8)];
        }

        var start = Math.Min(StartOffsetSeconds, knownDuration * 0.1);
        var endMargin = Math.Min(0.25, knownDuration * 0.05);
        var end = Math.Min(MaxSampleSpanSeconds, Math.Max(start + 0.1, knownDuration - endMargin));
        var span = Math.Max(0.1, end - start);

        var sampleCount = span switch
        {
            < 1.0 => 2,
            < 2.5 => 3,
            _ => DesiredSampleCount
        };

        return Enumerable.Range(0, sampleCount)
            .Select(index => sampleCount == 1
                ? start
                : start + (span * index / (sampleCount - 1)))
            .ToArray();
    }

    private async Task<FrameAttempt> ExtractFrameSafelyAsync(
        string inputPath,
        string outputPath,
        double timestampSeconds,
        CancellationToken extractionToken,
        CancellationToken callerToken)
    {
        try
        {
            await ExtractFrameAsync(
                inputPath,
                outputPath,
                timestampSeconds,
                extractionToken);

            return new FrameAttempt(
                new FastCompositeFrame(outputPath, timestampSeconds),
                null);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return new FrameAttempt(
                null,
                $"The seek near {timestampSeconds:0.##} seconds did not finish in time.");
        }
        catch (Exception ex)
        {
            return new FrameAttempt(
                null,
                $"The seek near {timestampSeconds:0.##} seconds failed: {ex.Message}");
        }
    }

    private static async Task ExtractFrameAsync(
        string inputPath,
        string outputPath,
        double timestampSeconds,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveToolPath("ffmpeg.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory
        };

        // -ss is deliberately before -i. That lets FFmpeg seek through the container to
        // a nearby keyframe instead of decoding every frame from the beginning. A tiny
        // amount of decode after the keyframe is acceptable because crop assistance does
        // not require frame-perfect timestamps.
        string[] arguments =
        [
            "-y",
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-ss", timestampSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-map", "0:v:0",
            "-frames:v", "1",
            "-sws_flags", "fast_bilinear",
            "-vf", $"scale=w='min(iw,{PreviewMaxDimension})':h='min(ih,{PreviewMaxDimension})':" +
                   "force_original_aspect_ratio=decrease:force_divisible_by=2",
            "-an",
            "-sn",
            "-dn",
            "-map_metadata", "-1",
            "-q:v", "4",
            "-update", "1",
            outputPath
        ];

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "FFmpeg could not be started to extract a composite preview frame. Install FFmpeg with " +
                "'winget install --id Gyan.FFmpeg -e', or place ffmpeg.exe next to the application.",
                ex);
        }

        var errorTask = process.StandardError.ReadToEndAsync();
        var outputTask = process.StandardOutput.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var standardError = await errorTask;
        _ = await outputTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}. {TrimDiagnostic(standardError)}");
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException("FFmpeg completed without producing a preview frame.");
        }
    }

    private static string ResolveToolPath(string executableName)
    {
        var appDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDirectory, executableName),
            Path.Combine(appDirectory, "tools", executableName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries and let Process.Start provide the final error.
            }
        }

        return executableName;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Timeout/cancellation cleanup is best effort.
        }
    }

    private static string TrimDiagnostic(string text)
    {
        const int maxLength = 1200;
        var trimmed = text.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return "..." + trimmed[^maxLength..];
    }

    private readonly record struct FrameAttempt(FastCompositeFrame? Frame, string? Error);
}

public readonly record struct FastCompositeFrame(string Path, double TimestampSeconds);

public sealed record FastCompositePreviewResult(
    IReadOnlyList<FastCompositeFrame> Frames,
    int RequestedSampleCount,
    double SampleSpanSeconds)
{
    public int SampleCount => Frames.Count;
}
