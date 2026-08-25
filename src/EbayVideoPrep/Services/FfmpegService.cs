using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using EbayVideoPrep.Models;

namespace EbayVideoPrep.Services;

public sealed class FfmpegService
{
    private const long EbayMaxFileSizeBytes = 150L * 1024L * 1024L;

    public async Task<VideoProbeInfo> ProbeAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var ffprobePath = ResolveToolPath("ffprobe.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory
        };

        string[] arguments =
        [
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height:stream_tags=rotate:stream_side_data=rotation",
            "-of", "json",
            inputPath
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
                "FFprobe could not be started. Install the complete FFmpeg package with " +
                "'winget install --id Gyan.FFmpeg -e', or place ffprobe.exe beside ffmpeg.exe.",
                ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var standardError = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFprobe exited with code {process.ExitCode}.\n\n{TrimDiagnostic(standardError)}");
        }

        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array ||
            streams.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("FFprobe did not find a video stream in this file.");
        }

        var stream = streams[0];
        if (!stream.TryGetProperty("width", out var widthElement) ||
            !stream.TryGetProperty("height", out var heightElement))
        {
            throw new InvalidOperationException("FFprobe could not determine the video's frame dimensions.");
        }

        var width = widthElement.GetInt32();
        var height = heightElement.GetInt32();
        var rotation = ReadClockwiseRotation(stream);

        return new VideoProbeInfo(width, height, rotation);
    }

    public async Task<CompositePreviewResult> CreateCompositePreviewAsync(
        string inputPath,
        string outputPath,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var ffmpegPath = ResolveToolPath("ffmpeg.exe");

        // One sample per second, capped at the first two minutes. Using the last frame
        // of tmix after reversing produces one equal-weight average of the sampled set.
        var sampleSeconds = duration > TimeSpan.Zero
            ? Math.Min(duration.TotalSeconds, 120.0)
            : 120.0;
        var sampleCount = Math.Clamp((int)Math.Floor(sampleSeconds), 1, 120);

        var filter =
            "fps=1," +
            "scale=w='min(iw,1280)':h='min(ih,1280)':" +
            "force_original_aspect_ratio=decrease:force_divisible_by=2," +
            $"tmix=frames={sampleCount}:weights='1',reverse";

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
            "-i", inputPath,
            "-t", sampleSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", "0:v:0",
            "-vf", filter,
            "-frames:v", "1",
            "-an",
            "-map_metadata", "-1",
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

        return new CompositePreviewResult(sampleCount);
    }

    public async Task<ExportResult> ExportAsync(
        string inputPath,
        string outputPath,
        CropRegion crop,
        CancellationToken cancellationToken = default)
    {
        var ffmpegPath = ResolveToolPath("ffmpeg.exe");
        var filter = BuildFilter(crop);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory
        };

        AddArguments(startInfo, inputPath, outputPath, filter);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "FFmpeg could not be started. Install FFmpeg with 'winget install --id Gyan.FFmpeg -e', " +
                "or place ffmpeg.exe next to the application or in a tools folder next to it.",
                ex);
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var standardError = await errorTask;
        _ = await outputTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}.\n\n{TrimDiagnostic(standardError)}");
        }

        var fileInfo = new FileInfo(outputPath);
        return new ExportResult(
            fileInfo.Length,
            fileInfo.Length > EbayMaxFileSizeBytes);
    }

    private static int ReadClockwiseRotation(JsonElement stream)
    {
        // Older MP4/MOV files commonly expose tags.rotate=90, where positive values
        // describe the clockwise display rotation expected by players.
        if (stream.TryGetProperty("tags", out var tags) &&
            tags.ValueKind == JsonValueKind.Object &&
            tags.TryGetProperty("rotate", out var rotateTag) &&
            TryReadDouble(rotateTag, out var tagRotation))
        {
            return NormalizeRightAngle(tagRotation);
        }

        // Newer FFprobe versions usually expose display-matrix side data instead.
        // FFprobe reports that rotation using the opposite sign from the traditional
        // MP4 rotate tag, so negate it to get the clockwise angle used by WPF here.
        if (stream.TryGetProperty("side_data_list", out var sideDataList) &&
            sideDataList.ValueKind == JsonValueKind.Array)
        {
            foreach (var sideData in sideDataList.EnumerateArray())
            {
                if (sideData.TryGetProperty("rotation", out var rotationElement) &&
                    TryReadDouble(rotationElement, out var sideDataRotation))
                {
                    return NormalizeRightAngle(-sideDataRotation);
                }
            }
        }

        return 0;
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        value = 0;
        return false;
    }

    private static int NormalizeRightAngle(double degrees)
    {
        var snapped = (int)Math.Round(degrees / 90.0, MidpointRounding.AwayFromZero) * 90;
        return ((snapped % 360) + 360) % 360;
    }

    private static string BuildFilter(CropRegion crop)
    {
        // FFmpeg applies display-rotation metadata automatically before -vf by default,
        // so crop coordinates are expressed in the same upright/display-oriented frame
        // the user sees in the preview.
        //
        // eBay currently documents a maximum upload resolution of 1080p. Bound the
        // result to 1920x1080, preserve aspect ratio, do not upscale, and keep dimensions
        // divisible by 2 for H.264/yuv420p compatibility.
        return $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}," +
               "scale=w='min(iw,1920)':h='min(ih,1080)':" +
               "force_original_aspect_ratio=decrease:force_divisible_by=2";
    }

    private static void AddArguments(
        ProcessStartInfo startInfo,
        string inputPath,
        string outputPath,
        string filter)
    {
        string[] arguments =
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", inputPath,
            "-map", "0:v:0",
            "-vf", filter,
            "-an",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", "18",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-map_metadata", "-1",
            outputPath
        ];

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
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

public readonly record struct VideoProbeInfo(int Width, int Height, int ClockwiseRotationDegrees);
public readonly record struct CompositePreviewResult(int SampleCount);
public readonly record struct ExportResult(long FileSizeBytes, bool ExceedsEbayFileSizeLimit);
