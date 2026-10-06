# Trawler — Requirements Document

**Date**: 2026-10-06  
**Target**: Emby Server plugin (net8.0)

---

## 1. Overview

A lightweight Emby Server plugin that automatically finds and downloads movie trailers from YouTube when new movies are added to the library. The downloaded trailer is saved alongside the movie file so Emby can display it natively during playback.

### Goals
- Zero-config: works out of the box with no user setup
- Resilient: survives YouTube HTML/API changes without breaking
- Lightweight: minimal memory footprint, no external dependencies beyond ffmpeg
- Silent on failure: a missing trailer should never block library scanning or crash Emby

---

## 2. Functional Requirements

### 2.1 Trigger Conditions

| ID | Requirement | Priority |
|----|-------------|----------|
| F-01 | On `ItemAdded` event for a `Movie` entity, begin trailer search after a configurable delay (default: 30s) to allow metadata providers to populate `RemoteTrailers` | Must |
| F-02 | Only trigger if the movie has no local trailer file already present in its folder (check for `*-trailer.mp4` or similar pattern) | Must |
| F-03 | Provide a scheduled task ("Download Missing Trailers") that scans all movies in the library and downloads trailers for any that are missing them | Should |
| F-04 | Support a manual "Download Trailer" action from the Emby UI (item context menu or plugin config page) | Could |

### 2.2 Trailer Discovery

| ID | Requirement | Priority |
|----|-------------|----------|
| F-10 | Use Emby's built-in `RemoteTrailers` array on the movie item as the primary source of YouTube URLs (these are populated by TheMovieDb / TMDb metadata provider) | Must |
| F-11 | If `RemoteTrailers` is empty after the delay, optionally perform a direct YouTube search using the movie title + year (e.g., `"Inception 2010 official trailer"`) | Should |
| F-12 | Iterate through all available trailer URLs until one produces a downloadable stream; do not stop at the first failure | Must |

### 2.3 Stream Resolution (YouTube)

| ID | Requirement | Priority |
|----|-------------|----------|
| F-20 | Extract the `videoId` from the YouTube watch URL (parse `?v=` or `/watch?v=` parameter) — this is the minimal input needed for the API path | Must |
| F-21 | Call the YouTube InnerTube API (`https://www.youtube.com/youtubei/v1/player`) using an **ANDROID_VR** client context (Oculus Quest 3, clientVersion `1.60.19`). This returns direct stream URLs with no signature ciphering | Must |
| F-22 | Obtain a `visitorData` token by calling the YouTube `sw.js_data` endpoint (`https://www.youtube.com/sw.js_data`) and parsing `[0][2][0][0][13]` from the response JSON. Cache this token for the lifetime of the plugin (or refresh every 4h) | Must |
| F-23 | Select the **best MP4 video stream** (highest resolution, `VideoType == Mp4`) and the **best AAC audio stream** (highest bitrate, `AdaptiveType == Audio`) from the API response's `streamingData.formats` + `adaptiveFormats` arrays | Must |
| F-24 | If the ANDROID_VR API returns no streams or an error, fall back to fetching the YouTube watch page HTML and parsing `ytInitialPlayerResponse` JSON as a secondary source | Should |
| F-25 | If both API and HTML paths fail, log a warning and move to the next trailer URL in the list | Must |

### 2.4 Download & Merge

| ID | Requirement | Priority |
|----|-------------|----------|
| F-30 | Download the video stream to a temp file using HTTP GET with appropriate User-Agent header | Must |
| F-31 | Download the audio stream to a temp file (same as above) | Must |
| F-32 | Merge video + audio into a single MP4 file using ffmpeg: `ffmpeg -i {video} -i {audio} -c:v copy -c:a aac -y {output}`. Use `-c:v copy` to avoid re-encoding (trailers are short, no quality loss) | Must |
| F-33 | Name the output file `{MovieName}-trailer.mp4` and place it in the same directory as the movie file | Must |
| F-34 | After successful merge, delete temp files | Must |
| F-35 | Trigger an Emby metadata refresh on the movie item so the new trailer is detected without a full library scan | Must |

### 2.5 Configuration

| ID | Requirement | Priority |
|----|-------------|----------|
| F-40 | Provide a plugin configuration page in Emby's admin UI (Settings > General > Plugins) | Should |
| F-41 | Configurable options: delay before download (seconds), ffmpeg path override, enable/disable auto-download, preferred quality (best / 720p max / 480p max) | Should |
| F-42 | Show last-run status and error count on the config page | Could |

---

## 3. Technical Architecture

### 3.1 High-Level Flow

```
+------------------------------------------------------------------+
| Emby Server                                                      |
|                                                                  |
|  LibraryManager.ItemAdded --> [Delay] --> Plugin                 |
|                                       |                          |
|                                       v                          |
|                              +-------------------+               |
|                              |   TrailerFinder    |              |
|                              |  (search logic)    |              |
|                              +---------+----------+              |
|                                        |                        |
|                                        v                        |
|                              +-------------------+               |
|                              |   YouTubeService   |             |
|                              |  (API + fallback)  |             |
|                              +---------+----------+              |
|                                        |                        |
|                                        v                        |
|                              +-------------------+               |
|                              |  DownloadMerger    |             |
|                              |  (HTTP + ffmpeg)   |             |
|                              +---------+----------+              |
|                                        |                        |
|                                        v                        |
|                              +-------------------+               |
|                              |   LibraryManager   |             |
|                              |   .RefreshItem()   |             |
|                              +-------------------+               |
+------------------------------------------------------------------+
```

### 3.2 Component Responsibilities

| Component | Responsibility |
|-----------|---------------|
| **EntryPoint** | Subscribe/unsubscribe to `ILibraryManager.ItemAdded`. Handle lifecycle (Run/Dispose). Fire-and-forget the download task with a delay. |
| **TrailerFinder** | Given a `BaseItem`, determine if it needs a trailer. Return list of candidate YouTube URLs from `RemoteTrailers` (and optionally search results). |
| **YouTubeService** | Given a YouTube watch URL, return `(videoUrl, audioUrl)` for the best streams. Encapsulates all YouTube interaction: visitor data, InnerTube API, HTML fallback, stream selection. |
| **DownloadMerger** | Given two URLs (video + audio), download to temp files, run ffmpeg merge, move result to final path. Clean up temps. |
| **ScheduledTask** | Implements `IScheduledTask`. Scans all `Movie` items, calls TrailerFinder for each missing one. Shows progress in Emby's task UI. |
| **PluginConfiguration** | Implements `IHasPluginConfiguration`. Stores user settings (delay, ffmpeg path, quality preference, enabled flag). |

### 3.3 YouTube API Details

#### InnerTube Player Request (Primary)

POST to `https://www.youtube.com/youtubei/v1/player?key=AIzaSyA8eiZmM1FaDVjRy-ds2MAqdkv2EEgdwLg`

Request body:
- `videoId`: the YouTube video ID
- `contentCheckOk`: true
- `context.client.clientName`: `"ANDROID_VR"`
- `context.client.clientVersion`: `"1.60.19"`
- `context.client.deviceMake`: `"Oculus"`
- `context.client.deviceModel`: `"Quest 3"`
- `context.client.osName`: `"Android"`
- `context.client.osVersion`: `"12L"`
- `context.client.platform`: `"MOBILE"`
- `context.client.hl`: `"en"`
- `context.client.gl`: `"US"`
- `context.client.utcOffsetMinutes`: 0
- `context.client.visitorData`: token from sw.js_data

User-Agent: `com.google.android.apps.youtube.vr.oculus/1.60.19 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip`

Response structure of interest:
- `playabilityStatus.status` — should be `"OK"`
- `streamingData.formats[]` — progressive (video+audio combined)
- `streamingData.adaptiveFormats[]` — separate video-only and audio-only streams

Stream selection logic:
- **Video**: From `adaptiveFormats`, filter where `mimeType` starts with `video/mp4`, sort by `height` descending, take first.
- **Audio**: From `adaptiveFormats`, filter where `mimeType == "audio/mp4"` (AAC), sort by `audioBitrate` descending, take first.

#### Visitor Data Token (sw.js_data)

GET `https://www.youtube.com/sw.js_data`
Cookie: `CONSENT=YES+cb.20210328-17-p0.en+FX+678`
User-Agent: same as above

Parse response as JSON array, index `[0][2][0][0][13]` is the visitor data string.

### 3.4 HTTP Client Strategy

| Concern | Decision |
|---------|----------|
| **Client lifetime** | Create one `HttpClient` per request (or use a shared instance with proper handler). Avoid socket exhaustion. Use `using` / `IDisposable` patterns strictly. |
| **User-Agent** | Use the ANDROID_VR UA for all YouTube requests. For HTML fallback, use a modern desktop Chrome UA. |
| **Cookies** | Send `CONSENT=YES+cb.20210328-17-p0.en+FX+678` on HTML page fetches to bypass EU consent wall. |
| **Timeouts** | 30s for API calls, 120s for stream downloads (trailers are short but bandwidth varies). |
| **Retries** | Retry API calls up to 2 times with exponential backoff (2s, 5s) on transient failures (429, 503). No retry on other 4xx. |

### 3.5 ffmpeg Integration

- Locate ffmpeg: check plugin config override > `ffmpeg` on PATH > common install locations
- Invoke via `Process.Start()` with redirected stdout/stderr for error capture
- Arguments: `-y -i {videoTemp} -i {audioTemp} -c:v copy -c:a aac {outputPath}`
- If ffmpeg not found, log error and skip (don't crash)
- Cleanup temp files in a `finally` block regardless of merge success

---

## 4. Error Handling & Resilience

| ID | Scenario | Behavior |
|----|----------|----------|
| E-01 | `RemoteTrailers` is empty | Log debug, skip (or trigger YouTube search if F-11 enabled) |
| E-02 | All trailer URLs fail to resolve streams | Log warning with movie name, no file created |
| E-03 | ANDROID_VR API returns `playabilityStatus != OK` | Log the status reason, try HTML fallback |
| E-04 | HTML fallback also fails | Log warning, move to next trailer URL |
| E-05 | Stream download fails (timeout, 404) | Retry once, then log and skip this URL |
| E-06 | ffmpeg not found or merge fails | Log error with ffmpeg stderr output, delete temps |
| E-07 | Plugin disposed while download in progress | Use `CancellationToken` propagated from Emby's shutdown |
| E-08 | Concurrent triggers (e.g., item added + scheduled task) | Use a per-item lock or check file existence before starting |

**General principle**: Wrap the entire per-item pipeline in a try/catch at the EntryPoint level. Log the exception but never let it propagate to Emby's event loop. A broken trailer download should never take down the media server.

---

## 5. Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| N-01 | **Performance**: Download + merge of a typical 2-minute trailer should complete in < 30 seconds on a 10 Mbps connection |
| N-02 | **Memory**: Peak additional memory usage < 50 MB (stream downloads go to disk, not RAM) |
| N-03 | **Thread safety**: Multiple movies added simultaneously should not conflict. Use per-item locks or a bounded concurrent queue (max 2-3 simultaneous downloads) |
| N-04 | **Logging**: Use Emby's `ILogger`. Log at Debug for normal flow, Info for success, Warning for skipped items, Error for exceptions. Include movie name in all log messages |
| N-05 | **No external NuGet deps** beyond what Emby already provides (System.Net.Http, Newtonsoft.Json or System.Text.Json). ffmpeg is the only external binary dependency |
| N-06 | **Target framework**: `netstandard2.0` for maximum Emby compatibility (or `net6.0` if targeting Emby 4.8+) |
| N-07 | **Plugin size**: < 5 MB installed (no bundled ffmpeg) |

---

## 6. Edge Cases

| Case | Handling |
|------|----------|
| Movie title contains special characters (quotes, slashes, unicode) | Sanitize filename: replace `\ / : * ? " < > \|` with `-`. Keep unicode letters/digits. |
| Trailer already exists but is 0 bytes (failed previous download) | Treat as missing; re-download and overwrite |
| YouTube returns a "video unavailable" or "join membership" status | Log specific reason, skip to next URL |
| Movie has no `RemoteTrailers` AND YouTube search disabled | Silently skip (debug log only) |
| Emby library path is on a network share (SMB/NFS) | ffmpeg temp files should go to local temp (`Path.GetTempPath()`), not the network path, to avoid lock issues. Move final file at the end. |
| Multiple trailers available (e.g., main + international) | Download only the first one that succeeds (highest priority in `RemoteTrailers` array) |

---

## 7. Acceptance Criteria

The plugin is considered complete when:

1. Adding a new movie to an Emby library results in a `{MovieName}-trailer.mp4` file appearing in the movie's folder within 60 seconds
2. The trailer plays correctly in Emby's web client (video + audio synced)
3. Removing the trailer file and running the scheduled task re-downloads it
4. Killing the network mid-download does not crash Emby or leave orphan temp files
5. Plugin can be disabled from the config page without requiring a server restart
6. All YouTube API failures are logged with enough detail to diagnose (status code, videoId, error message)

---

## 8. Out of Scope (v1)

- TV Show episode trailers
- Music video downloads
- Subtitle/caption embedding
- Trailer quality transcoding (always use source quality)
- YouTube channel-based search (rely on Emby's RemoteTrailers)
- Jellyfin compatibility (Emby-specific APIs used)
- Background "pre-download" of upcoming release trailers

---

## 9. Suggested Project Structure

```
TrailerGrabber/
+-- TrailerGrabber.csproj
+-- Plugin.cs                          # IPlugin implementation, version/metadata
+-- Configuration/
|   +-- PluginConfiguration.cs         # IHasPluginConfiguration
+-- EntryPoints/
|   +-- EntryPoint.cs                  # ILibraryManager event subscription
+-- Services/
|   +-- TrailerFinder.cs               # Find candidate URLs for an item
|   +-- YouTubeService.cs              # InnerTube API + HTML fallback
|   |   +-- VisitorDataCache.cs        # sw.js_data token caching
|   |   +-- StreamSelector.cs          # Pick best video+audio from response
|   +-- DownloadMerger.cs             # HTTP download + ffmpeg merge
+-- Tasks/
|   +-- DownloadMissingTrailersTask.cs # IScheduledTask
+-- Web/
|   +-- configPage.html               # Plugin settings UI
|   +-- configPage.js
+-- Properties/
    +-- AssemblyInfo.cs
```

---

## 10. Design Principles (Anti-Patterns Avoided)

| Anti-pattern (avoided) | Trawler does instead |
|--------------------------|------------|
| HTML scraping was the **primary** path; API was secondary | Make the InnerTube API the **primary** path. HTML is fragile and changes without notice |
| `MakeClient()` created an HttpClient, added headers, then returned a *different* client | Use a single factory method that returns the configured instance |
| No `using` / dispose on HttpClient in error paths | Always use `using` or try/finally for all IDisposable resources |
| Hardcoded regex patterns for HTML parsing with no fallback | Prefer structured API responses. If scraping, wrap in try/catch and degrade gracefully |
| License validation could block the entire plugin if the license server was down | Make licensing optional or cache a valid state locally |
| Unsubscribed from an event that was never subscribed (Dispose bug) | Keep subscription/unsubscription symmetric; test the Dispose path |
| No retry logic on transient HTTP failures | Add exponential backoff for 429/503 responses |
| Temp files written to the movie's directory (network path risk) | Write temps to local `Path.GetTempPath()`, move final file at end |
