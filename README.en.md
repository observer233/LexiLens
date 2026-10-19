# LexiLens

A Windows screen translation tool with a bilingual overlay and an English vocabulary list.

[Download](https://github.com/observer233/LexiLens/releases/latest) · [中文](README.md) · [Setup](docs/USER_GUIDE.md) · [Bug analysis](docs/BUG_ANALYSIS.md)

![LexiLens v0.4.0](docs/images/main.png)

## v0.4.0

- Hover to highlight a window, then click to select its visible area. Manual subtitle selection remains available.
- Translate once or continuously. Minor OCR jitter no longer prevents translation indefinitely.
- Capture, recognition, text gating, translation and overlay have separate status indicators.
- Rotating metadata logs and an exportable JSON diagnostic report, without screenshots, game text, keys, endpoint addresses or window titles.
- PaddleOCR runs in a separate CPU process. A watchdog terminates a stalled helper and tries Windows OCR. Empty enhanced results also try Windows OCR.
- A light Fluent interface, concise settings, and a three-step getting-started panel.

## Use

1. Download the Windows x64 ZIP, extract everything and run `LexiLens.exe`. .NET and OCR models are included.
2. Choose a service and test/save it. MyMemory needs no key; cloud providers use your own API key and credits.
3. Select a window or drag a subtitle region, then try a single translation before starting continuous translation.
4. If translation stalls, inspect the five stage indicators and open Diagnostics.

Shortcuts use **Ctrl+Alt**: **W** select window, **R** drag region, **F** translate once, **T** start/stop, **H** hide/show overlay, **L** toggle click-through.

Selection does not track later window moves or resizing. A window spanning multiple monitors is clipped to its largest visible monitor segment. Large selections cost more OCR time and API tokens.

## Recognition and providers

English/Chinese enhanced recognition uses bundled CPU PaddleOCR v5; Windows OCR is also available and handles other installed languages. Stylized or obscured text can still be misread. Recognition inspection displays the crop and raw OCR without calling a translation API.

25 cloud presets, a custom Chat Completions endpoint, MyMemory and Ollama are supported. See [provider setup](docs/AI_PLATFORMS.md). Presets are not shared keys or a promise of free API access. Ollama translation models are optional separate downloads; OCR models are included.

Screenshots stay on-device. Recognized text is sent to the chosen translation service. Keys are encrypted for the current Windows user and isolated by provider/endpoint. Settings, vocabulary and logs live in `%LOCALAPPDATA%\LexiLens`.

## Verification

73 core tests pass. Desktop diagnostics cover actual capture, isolated OCR, live MyMemory translation, window snapping, click-through, deliberate worker stalls/cancellation, fallback and diagnostic export. Native inference is bounded by a 15-second initial / 8-second later timeout. Missing VC++ x64 runtimes can be addressed through the Microsoft link in Settings.

The v0.3.0 wait condition was reproduced: alternating OCR results produced zero API calls; the fix produced one. This identifies a code defect, not proof that every user's waiting screen has the same cause. See [bug analysis](docs/BUG_ANALYSIS.md), [project research](docs/RESEARCH.md) and [test records](docs/TESTING.md).

Per-game, HDR, mixed-DPI and long-running compatibility remain unverified. Provider protocols have mock tests; real keys have not been tested for every platform. Releases are unsigned.

## Build

Windows and .NET 10 SDK:

```powershell
dotnet build LexiLens.sln -c Release
dotnet test tests/LexiLens.Core.Tests -c Release
dotnet run --project src/LexiLens.App
./scripts/publish.ps1
```

[MIT License](LICENSE). [Third-party runtimes and models](docs/THIRD_PARTY_NOTICES.md) retain their respective licenses.
