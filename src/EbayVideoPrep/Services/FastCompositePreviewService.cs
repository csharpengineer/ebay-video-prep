using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace EbayVideoPrep.Services;

/// <summary>
/// Builds a quick turntable-oriented crop aid. It intentionally favors speed and
/// a crisp anchor frame over a mathematically even average of the whole video.
/// </summary>
public sealed class FastCompositePreviewService
{
    private const double MaxSampleSpanSeconds = 10.0;
    private const double StartOffsetSeconds = 0.25;
    private const int PreviewMaxDimension = 720;
    private const int AnchorWeight = 5;

    public async Task<FastCompositePreviewResult> CreateAsync(
        string inputPath,
        string outputPath,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var ffmpegPath = ResolveToolPath("ffmpeg.exe");

        var knownDurationSeconds = duration > TimeSpan.Zero
            ? duration.TotalSeconds
            : MaxSampleSpanSeconds + StartOffsetSeconds;

        var startOffset = knownDurationSeconds > 0.75
            ? StartOffsetSeconds
            : 0.0;
        var remainingSeconds = Math.Max(0.1, knownDurationSeconds - startOffset);
        var sampleSpanSeconds = Math.Min(remainingSeconds, MaxSampleSpanSeconds);

        // Four snapshots are enough to expose front/side/back crop extents for the
        // common 3-4 RPM turntables this app is aimed at. Very short clips use fewer.
        var sampleCount = sampleSpanSeconds switch
        {
            < 0.75 => 2,
            < 1.5 => 3,
            _ => 4
        };

        // Leave a little room before EOF so the final sample is reliably available.
        var endMargin = Math.Min(0.25, sampleSpanSeconds * 0.1);
        var coveredSpanSeconds = Math.Max(0.05, sampleSpanSeconds - endMargin);
        var sampleIntervalSeconds = sampleCount > 1
            ? coveredSpanSeconds / (sampleCount - 1)
            : coveredSpanSeconds;

        var intervalText = sampleIntervalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        var weights = string.Join(' ', Enumerable.Range(0, sampleCount)
            .Select(index => index == 0 ? AnchorWeight.ToString(CultureInfo.InvariantCulture) : "1"));

        // select happens before scale/tmix, so FFmpeg decodes only the short beginning
        // of the clip and does expensive image work on just 2-4 frames. The first
        // selected frame gets a strong weight, keeping the product recognizable while
        // later angles appear as light ghosts around its motion envelope.
        var filter =
            $"select='isnan(prev_selected_t)+gte(t-prev_selected_t,{intervalText})'," +
            $"scale=w='min(iw,{PreviewMaxDimension})':h='min(ih,{PreviewMaxDimension})':" +
            "force_original_aspect_ratio=decrease:force_divisible_by=2," +
            $"tmix=frames={sampleCount}:weights='{weights}'," +
            $"select='eq(n,{sampleCount - 1})'";

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory
        };

        string[] arguments =
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-ss", startOffset.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-t", sampleSpanSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0:v:0",
            "-vf", filter,
            "-frames:v", "1",
            "-an",
            "-map_metadata", "-1",
            "-compression_level", "2",
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
                "FFmpeg could not be started to build the composite preview. Install FFmpeg with " +
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
                $"FFmpeg could not build the composite preview (exit code {process.ExitCode}).\n\n" +
                TrimDiagnostic(standardError));
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException("FFmpeg completed without producing a composite preview image.");
        }

        return new FastCompositePreviewResult(sampleCount, coveredSpanSeconds);
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
            // Cancellation cleanup is best effort.
        }
    }

    private static string TrimDiagnostic(string text)
    {
        const int maxLength = 3000;
        if (text.Length <= maxLength)
        {
            return text;
        }

        return "..." + text[^maxLength..];
    }
}

public readonly record struct FastCompositePreviewResult(int SampleCount, double SampleSpanSeconds);
