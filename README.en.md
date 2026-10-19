# LexiLens

**Play in English. Understand the story. Keep the words.**

LexiLens is a Windows desktop translation overlay for language learners who want to keep their games in English without repeatedly pausing to look up unfamiliar vocabulary.

Select a subtitle or quest region, read the original text alongside a Simplified Chinese translation, and save unfamiliar English words with their context.

![LexiLens desktop application with a labeled sample preview](docs/images/main.png)

## v0.2.0

- Native WPF app with local Windows OCR and GDI screen capture.
- Region selection on individual monitors, including monitors with negative coordinates.
- Movable, resizable, always-on-top bilingual overlay with mouse click-through.
- Global shortcuts for start/pause, region selection, visibility and click-through.
- Two matching OCR scans before translation, in-memory deduplication, cancellation of superseded requests, and stale-result protection.
- Setup wizard with MyMemory, Ollama, 24 cloud-platform presets, and a custom compatible endpoint. Bring your own API key; keys are encrypted for the current Windows user and isolated by provider and endpoint.
- Model discovery, connection tests, separate free-tier/trial/paid classifications, and local model detection/download controls. No automatic provider or paid-model fallback.
- Click English words to look them up, edit definitions, save their context and export vocabulary as CSV.
- A small practice window to try the real capture and OCR workflow before launching a game.

**Early working prototype.** Desktop capture, OCR, a live MyMemory translation, and overlay behavior have been verified on a Windows practice window. Compatibility with individual games, HDR and mixed-DPI displays has not been established. The initial preview contains a fixed example and is explicitly labeled.

## Cloud platforms

See the [platform catalog and conditions](docs/AI_PLATFORMS.md). A free tier, a trial credit, and a paid API are different. The app includes endpoint presets, not shared keys or guaranteed free access. Most providers require an individual account/API key. Account billing settings can still incur charges even for a model with a free tier; set provider-side spending controls.

Protocol simulations cover the cloud adapters; real credentials have not been tested for every provider.

## Try it

1. Download the portable Windows x64 ZIP from [Releases](https://github.com/observer233/LexiLens/releases/latest), extract it and run `LexiLens.exe`. The .NET runtime is included.
2. Use Windows 10 build 19041 or newer, preferably Windows 11, with the English Windows OCR language feature installed.
3. Open the practice window, select its English text using the region button, and press `Ctrl+Alt+T`.
4. Use borderless/windowed mode in games. Press `Ctrl+Alt+L` to unlock the overlay for dragging or word lookup.

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+T | Start / pause |
| Ctrl+Alt+R | Select region; pauses the current session |
| Ctrl+Alt+H | Show / hide overlay; translation continues while hidden |
| Ctrl+Alt+L | Toggle mouse click-through |
| Esc | Cancel region selection |

The default provider sends **recognized text to MyMemory**. Screenshots stay on the device and are not saved during normal use. MyMemory has usage limits and is intended here for quick trials and occasional quest text; continuous subtitles may exhaust its quota. Local Ollama keeps text on the device when configured with a loopback endpoint; remote endpoints receive the recognized text. Models must be downloaded separately.

Settings, explicitly saved vocabulary/context, and a Windows DPAPI encrypted credential vault are persisted under `%LOCALAPPDATA%\LexiLens`. There is no telemetry, game injection, memory reading, driver installation or administrator requirement.

## Limitations

This is periodic OCR assistance, not frame-by-frame or zero-latency translation. The default scan interval is 700 ms; stable-frame detection and provider inference/network latency add to it. Very short subtitles may be missed. Stylized, small or obscured text can reduce OCR accuracy. Select one region on one monitor, and reselect after the game moves or display configuration changes. Exclusive fullscreen and protected content may not work.

The first version has no custom shortcuts, game glossary profiles, pronunciation, spaced repetition, signed installer or broad game compatibility test results. English word lookup is supported; other installed OCR languages can be translated but have no dedicated word segmentation workflow.

## Build

Windows and [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
dotnet build LexiLens.sln -c Release
dotnet test tests/LexiLens.Core.Tests -c Release
dotnet run --project src/LexiLens.App
./scripts/publish.ps1
```

See the [Chinese README](README.md), [user guide](docs/USER_GUIDE.md) and [verification notes](docs/TESTING.md) for setup, privacy and limitations.

[MIT License](LICENSE). Third-party runtimes, model weights and external services retain their own licenses and terms.
