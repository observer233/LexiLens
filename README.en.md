# LexiLens

**Play in English. Understand the story. Keep the words.**

LexiLens is a Windows desktop translation overlay for language learners who want to keep their games in English without repeatedly pausing to look up unfamiliar vocabulary.

Select a subtitle or quest region, read the original text alongside a Simplified Chinese translation, and save unfamiliar English words with their context.

![LexiLens desktop application with a labeled sample preview](docs/images/main.png)

## v0.3.0

- Native WPF app with bundled CPU PaddleOCR v5 for English/Chinese, contrast/polarity enhancement, small-text upscaling, and a Windows OCR option. No Python, GPU or separate OCR model download required.
- Recognition inspection shows the selected crop, raw OCR, engine and duration without calling translation APIs.
- Region selection on individual monitors, including monitors with negative coordinates.
- Movable, resizable, always-on-top bilingual overlay with mouse click-through.
- Global shortcuts for start/pause, region selection, visibility and click-through.
- Two matching OCR scans before translation, in-memory deduplication, cancellation of superseded requests, and stale-result protection.
- Setup wizard with MyMemory, Ollama, 25 cloud-platform presets, and a custom compatible endpoint. All platforms are visible by default, with shortcuts for DeepSeek, Doubao, Qwen, ChatGPT/OpenAI, Claude, Gemini and Grok. Regional endpoints are available for Qwen, Kimi and MiniMax. Bring your own API key; keys are encrypted and isolated by provider and endpoint.
- Model discovery, connection tests, separate free-tier/trial/paid classifications, and local model detection/download controls. No automatic provider or paid-model fallback.
- Click English words to look them up, edit definitions, save their context and export vocabulary as CSV.
- A small practice window to try the real capture and OCR workflow before launching a game.

**Early working prototype.** Desktop capture, OCR, a live MyMemory translation, and overlay behavior have been verified on a Windows practice window. Compatibility with individual games, HDR and mixed-DPI displays has not been established. The initial preview contains a fixed example and is explicitly labeled.

## Cloud platforms

See the [platform catalog and conditions](docs/AI_PLATFORMS.md). A free tier, a trial credit, and a paid API are different. The app includes endpoint presets, not shared keys or guaranteed free access. Most providers require an individual account/API key. Account billing settings can still incur charges even for a model with a free tier; set provider-side spending controls.

Protocol simulations cover the cloud adapters; real credentials have not been tested for every provider.

## Try it

1. Download the portable Windows x64 ZIP from [Releases](https://github.com/observer233/LexiLens/releases/latest), extract it and run `LexiLens.exe`. The .NET runtime is included.
2. Use Windows 10 build 19041 or newer, preferably Windows 11, x64. English/Chinese enhanced OCR is bundled; the Windows OCR option and other languages need Windows language features. Missing VC++ x64 runtimes can be installed through the Microsoft link in preferences.
3. Open the practice window, select its English text using the region button, and press `Ctrl+Alt+T`.
4. Use borderless/windowed mode in games. Press `Ctrl+Alt+L` to unlock the overlay for dragging or word lookup.

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+T | Start / pause |
| Ctrl+Alt+R | Select region; pauses the current session |
| Ctrl+Alt+H | Show / hide overlay; translation continues while hidden |
| Ctrl+Alt+L | Toggle mouse click-through |
| Esc | Cancel region selection |

The default provider sends **recognized text to MyMemory**. Screenshots stay on the device and are not saved during normal use. MyMemory has usage limits. Local Ollama keeps text on the device with a loopback endpoint; remote endpoints receive recognized text. Translation models must be downloaded separately; OCR models are included.

Settings, explicitly saved vocabulary/context, and a Windows DPAPI encrypted credential vault are persisted under `%LOCALAPPDATA%\LexiLens`. There is no telemetry, game injection, memory reading, driver installation or administrator requirement.

## Limitations

This is periodic OCR assistance, not frame-by-frame translation. The default scan interval is 700 ms; OCR and translation latency add to it. Eight synthetic font samples showed character error rates of 22.7% with Windows OCR versus 0.8% with enhanced OCR, ignoring punctuation/whitespace/case. This limited test does not establish accuracy across games. Similar letters, stylized or obscured text can still be misread. Inspect OCR before choosing a translation provider. Select one region on one monitor and reselect after display changes. Exclusive fullscreen and protected content may not work. See [OCR measurements](docs/OCR.md).

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
