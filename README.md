# eBay Video Prep

A small Windows utility for preparing product videos for eBay listings.

Drag in an MP4 or MOV, preview it continuously, position and resize a visual crop box, and export a cropped, muted H.264 MP4 suitable for upload to eBay.

## Goals

- Drag-and-drop or open a video file.
- Loop the video continuously while editing.
- Move and resize a crop rectangle directly over the preview.
- Quickly reset to the full frame or choose a centered square crop.
- Export to MP4/H.264 with audio removed.
- Keep the output within a 1920x1080 bounding box without upscaling.
- Warn when the resulting file exceeds eBay's 150 MB upload limit.

## Requirements

- Windows 10 or Windows 11.
- .NET 9 Desktop Runtime when using a framework-dependent build.
- FFmpeg available either:
  - as `ffmpeg.exe` next to the app;
  - at `tools\ffmpeg.exe` next to the app; or
  - on `PATH`.

FFmpeg can be installed with Windows Package Manager:

```powershell
winget install --id Gyan.FFmpeg -e
```

## Build

```powershell
dotnet build .\EbayVideoPrep.sln -c Release
```

## Usage

1. Launch **eBay Video Prep**.
2. Drag an `.mp4` or `.mov` into the window, or click **Open Video**.
3. Drag inside the crop rectangle to move it.
4. Drag an edge or corner handle to resize it.
5. Click **Save eBay MP4**.
6. Choose the output filename.

The export is re-encoded as H.264 because cropping requires re-encoding. Audio is removed, the output pixel format is broadly compatible `yuv420p`, and `faststart` is enabled for web playback.

## eBay video constraints

The application is intentionally conservative about eBay's published listing-video requirements. At the time this project was created, eBay documents a maximum file size of 150 MB, a maximum upload resolution of 1080p, and MP4 using MPEG-4 AVC/H.264 as a supported format.

Always check eBay's current documentation if an upload is rejected, since platform requirements can change.

## Status

Early MVP. The first version intentionally stays focused on one job: **crop a product video visually and save a quiet, upload-ready copy quickly**.

Likely follow-up features include batch processing, reusable crop presets, rotation, duration trimming, and automatic bitrate targeting for the 150 MB limit.

## Trademark notice

This project is an independent utility and is not affiliated with, endorsed by, or sponsored by eBay Inc. eBay is a trademark of eBay Inc.
