# eBay Video Prep

A small Windows utility for preparing product videos for eBay listings.

Drag in an MP4 or MOV, use a turntable-friendly composite preview to find the right crop quickly, switch to a looping video when needed, and export a cropped, muted H.264 MP4 suitable for upload to eBay.

## Goals

- Drag-and-drop or open a video file.
- Default to **Composite View** for fast crop setup on turntable recordings.
- Build the composite from one frame per second, up to the first 120 seconds.
- Average the sampled frames so static background/turntable areas remain stable while the rotating item shows its overall motion envelope.
- Switch to **Loop View** when live playback is useful.
- Scrub backward and forward through the loop with a timeline slider.
- Honor phone display-rotation metadata so portrait recordings preview upright.
- Move and resize a transparent crop rectangle directly over either preview mode.
- Quickly reset to the full frame or choose a centered square crop.
- Export to MP4/H.264 with audio removed.
- Keep the output within a 1920x1080 bounding box without upscaling.
- Warn when the resulting file exceeds eBay's 150 MB upload limit.

## Requirements

- Windows 10 or Windows 11.
- .NET 9 Desktop Runtime when using a framework-dependent build.
- FFmpeg/FFprobe available either:
  - as `ffmpeg.exe` and `ffprobe.exe` next to the app;
  - in the `tools` folder next to the app; or
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
3. The app reads the video's display orientation and shows phone footage upright when rotation metadata is present.
4. **Composite View** is selected by default. The live video remains visible briefly while FFmpeg builds an average from one frame per second (maximum 120 frames).
5. Use the ghosted composite to place the crop around the item's full turntable motion.
6. Switch to **Loop** if you want to inspect the original moving video; use the timeline to scrub to any point.
7. Drag inside the crop rectangle to move it, or drag an edge/corner handle to resize it.
8. Click **Save eBay MP4** and choose the output filename.

The composite is only a preview aid and is never written into the exported video. Export keeps the original timing, re-encodes as H.264 because cropping requires re-encoding, removes audio, uses broadly compatible `yuv420p`, and enables `faststart` for web playback. Crop coordinates are calculated against the display-oriented frame so portrait phone footage exports the same region shown in the preview.

## Composite View

Composite View is aimed primarily at videos recorded with a stationary camera and turntable. FFmpeg samples one upright frame per second and averages those frames together. Static parts of the scene reinforce each other, while the rotating product appears as a ghosted composite showing the area it occupies throughout the recording.

Only the first two minutes are sampled. The generated image is preview-sized (bounded to 1280 pixels on either axis) and kept in memory after generation, so switching between Composite and Loop is immediate after the first build.

## eBay video constraints

The application is intentionally conservative about eBay's published listing-video requirements. At the time this project was created, eBay documents a maximum file size of 150 MB, a maximum upload resolution of 1080p, and MP4 using MPEG-4 AVC/H.264 as a supported format.

Always check eBay's current documentation if an upload is rejected, since platform requirements can change.

## Status

Early MVP focused on one job: **find a safe crop for a product video quickly and save a quiet, upload-ready copy**.

Likely follow-up features include reusable crop presets, automatic crop suggestions from the composite, manual rotation correction, duration trimming, batch processing, and automatic bitrate targeting for the 150 MB limit.

## Trademark notice

This project is an independent utility and is not affiliated with, endorsed by, or sponsored by eBay Inc. eBay is a trademark of eBay Inc.
