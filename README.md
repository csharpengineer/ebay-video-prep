# eBay Video Prep

A small Windows utility for preparing product videos for eBay listings.

Drag in an MP4 or MOV, use a turntable-friendly composite preview to find the right crop quickly, switch to a looping video when needed, and export a cropped, muted H.264 MP4 suitable for upload to eBay.

## Goals

- Drag-and-drop or open a video file.
- Default to **Composite View** for fast crop setup on turntable recordings.
- Build the composite from a few direct point-seek snapshots rather than decoding a continuous chunk of the video.
- Extract up to four preview frames in parallel using FFmpeg input-side seeking.
- Keep the first sampled angle crisp and dominant while later angles appear as light ghosts showing the product's motion envelope.
- Keep partial composites when 2-3 of the requested snapshots succeed instead of discarding all useful work.
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
4. **Composite View** is selected by default. The live video remains usable while the app grabs a few direct snapshots in parallel.
5. Use the ghost composite to place the crop around the item's full turntable motion.
6. Switch to **Loop** if you want to inspect the original moving video; use the timeline to scrub to any point.
7. Drag inside the crop rectangle to move it, or drag an edge/corner handle to resize it.
8. Click **Save eBay MP4** and choose the output filename.

The composite is only a preview aid and is never written into the exported video. Export keeps the original timing, re-encodes as H.264 because cropping requires re-encoding, removes audio, uses broadly compatible `yuv420p`, and enables `faststart` for web playback. Crop coordinates are calculated against the display-oriented frame so portrait phone footage exports the same region shown in the preview.

## Composite View

Composite View is aimed primarily at videos recorded with a stationary camera and a roughly 3-4 RPM turntable. The app chooses up to four timestamps spread across roughly the first 10 seconds of the recording.

Each timestamp is extracted by its own FFmpeg process with `-ss` placed before `-i`. This uses container/keyframe seeking instead of decoding every preceding frame. The point seeks run concurrently, and each thumbnail is scaled to a maximum of 720 pixels on either axis.

The first successful frame is drawn normally. Later turntable angles are layered over it at low opacity, so the product remains recognizable while side profiles, protrusions, and other crop-critical motion appear as ghosts around the anchor view.

The frame-grab stage has a short time budget. If two or three snapshots finish while another seek is slow, the app uses the successful frames rather than throwing the whole composite away. If fewer than two frames are available, it falls back to Loop because a single still image does not provide meaningful motion-envelope information.

The generated composite is kept in memory after generation, so switching between Composite and Loop is immediate after the first build.

## eBay video constraints

The application is intentionally conservative about eBay's published listing-video requirements. At the time this project was created, eBay documents a maximum file size of 150 MB, a maximum upload resolution of 1080p, and MP4 using MPEG-4 AVC/H.264 as a supported format.

Always check eBay's current documentation if an upload is rejected, since platform requirements can change.

## Status

Early MVP focused on one job: **find a safe crop for a product video quickly and save a quiet, upload-ready copy**.

Likely follow-up features include reusable crop presets, automatic crop suggestions from the composite, manual rotation correction, duration trimming, batch processing, and automatic bitrate targeting for the 150 MB limit.

## Trademark notice

This project is an independent utility and is not affiliated with, endorsed by, or sponsored by eBay Inc. eBay is a trademark of eBay Inc.
