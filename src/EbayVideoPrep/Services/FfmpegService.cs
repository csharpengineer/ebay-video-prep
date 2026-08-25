using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using EbayVideoPrep.Models;

namespace EbayVideoPrep.Services;

public sealed class FfmpegService
{
    private const long EbayMaxFileSizeBytes = 150L * 1024L * 1024L;

    public async Task<ExportResult> ExportAsync(
        string inputPath,
        string outputPath,
        CropRegion crop,
        CancellationToken cancellationToken = default)
    {
        var ffmpegPath = ResolveFfmpegPath();
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

    private static string BuildFilter(CropRegion crop)
    {
        // eBay currently documents a maximum upload resolution of 1080p.
        // Bound the result to 1920x1080, preserve aspect ratio, do not upscale,
        // and keep dimensions divisible by 2 for H.264/yuv420p compatibility.
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

    private static string ResolveFfmpegPath()
    {
        var appDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDirectory, "ffmpeg.exe"),
            Path.Combine(appDirectory, "tools", "ffmpeg.exe")
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
                var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
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

        return "ffmpeg.exe";
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

public readonly record struct ExportResult(long FileSizeBytes, bool ExceedsEbayFileSizeLimit);
