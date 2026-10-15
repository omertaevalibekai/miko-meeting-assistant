# Build Miko from source

Requirements: Windows 10/11 x64, .NET Framework 4.8, and PowerShell 5.1 or newer. The build uses the system C# compiler. No separate .NET SDK, Python or Node.js installation is required.

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\Miko.exe
```

This downloads NAudio 2.2.1 from NuGet, verifies its pinned SHA-256 checksums and builds Miko with recording and OpenAI Whisper API support. Enter your API key only in **⋯ → Settings**; never add it to source files.

To also include the offline Whisper base engine and models:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -WithLocalWhisper
```

The first run downloads whisper.cpp b5130 and approximately 150 MB of models. Downloads are checked against pinned SHA-256 values. Subsequent builds use `.deps/downloads`. Sources and notices are listed in [DEPENDENCIES.md](DEPENDENCIES.md) and [licenses/](licenses/).

To rebuild without network access after dependencies have been restored:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\source\build.ps1
```

## Run tests

These checks do not start microphone capture or send real OpenAI requests. API tests use a fake HTTP handler.

```powershell
Start-Process .\Miko.exe -ArgumentList '--self-test work\core-tests' -WindowStyle Hidden -Wait
Start-Process .\Miko.exe -ArgumentList '--api-tests work\api-tests' -WindowStyle Hidden -Wait
Start-Process .\Miko.exe -ArgumentList '--ui-test work\ui-tests' -WindowStyle Hidden -Wait
```

Results are written to `work/core-tests/results.txt`, `work/api-tests/api-results.txt` and `work/ui-tests/ui-results.txt`. A nonzero process exit code indicates failure. See [TEST-RESULTS.md](TEST-RESULTS.md) for additional local-engine checks.

## Regenerate preview images

```powershell
Start-Process .\Miko.exe -ArgumentList '--render work\screenshots' -WindowStyle Hidden -Wait
Start-Process .\Miko.exe -ArgumentList '--animation-preview work\mascot-frames' -WindowStyle Hidden -Wait
Start-Process .\Miko.exe -ArgumentList '--state-preview work\state-frames' -WindowStyle Hidden -Wait
```

Rendering uses isolated demo settings and generic device labels. It does not load personal meeting content or API keys. Each animation command exports 60 PNG frames at 12 fps. With FFmpeg installed, make a GIF:

```powershell
ffmpeg -framerate 12 -i work/mascot-frames/frame-%03d.png -filter_complex '[0:v]split[a][b];[a]palettegen[p];[b][p]paletteuse' -loop 0 preview/mascots-animated.gif
```

## Repository layout

- `source/`: WPF interface, pixel mascots, WASAPI recording, processing and tests.
- `scripts/`: verified dependency restoration.
- `preview/`: app-rendered images, animations and non-personal test reports.
- `licenses/`: third-party license texts.

Executables, dependencies, models, recordings, settings, drafts, API keys and caches are excluded by `.gitignore`. Ready-to-run builds are distributed through GitHub Releases. Extract and move the entire release folder together.
