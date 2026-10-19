# LexiLens

A Windows screen translation tool with a bilingual overlay and an English vocabulary list.

[Download](https://github.com/observer233/LexiLens/releases/latest) · [中文](README.md) · [Setup](docs/USER_GUIDE.md) · [Bug analysis](docs/BUG_ANALYSIS.md)

![LexiLens v0.5.0](docs/images/main.png)

## v0.5.0

- Fast subtitles: 150ms capture cadence, Windows OCR first, immediate submission on the first recognized frame. Accuracy mode retains the enhanced OCR workflow.
- Up to four named capture areas with separate movable overlays, automatic initial placement near each crop, collision avoidance and saved positions.
- In-memory dialogue replay for the last 100 completed pairs. New subtitles no longer cancel earlier captured dialogue; late replies cannot replace newer results.
- Shared cache, up to two in-flight requests and a configurable API start interval (400ms by default in fast mode). HTTP 429 cooldown is still enforced.
- Area-numbered diagnostic metadata; keys, screenshots and game text stay out of reports.

## Use

1. Download the Windows x64 ZIP, extract everything and run `LexiLens.exe`. .NET and OCR models are included.
2. Choose a service and test/save it. MyMemory needs no key; cloud providers use your own API key and credits.
3. Select a window or drag a subtitle region, then try a single translation before starting continuous translation.
4. Use **Add region** for separate quest/subtitle areas. **Fast subtitles** submits immediately; **Dialogue replay** keeps captured lines available after a cutscene.
5. If translation stalls, inspect the five stage indicators and open Diagnostics.

Shortcuts use **Ctrl+Alt**: **W** select window, **R** replace region, **A** add region, **F** translate once, **T** start/stop, **H** hide/show overlay, **L** toggle click-through.

Selection does not track later window moves or resizing. A window spanning multiple monitors is clipped to its largest visible monitor segment. Large selections cost more OCR time and API tokens.

## Recognition and providers

Fast mode tries Windows OCR first and enhanced OCR on empty results. Disable fast mode for stylized fonts. English/Chinese enhanced recognition uses bundled CPU PaddleOCR v5; Windows OCR is also available and handles other installed languages. Stylized or obscured text can still be misread. Recognition inspection displays the crop and raw OCR without calling a translation API.

25 cloud presets, a custom Chat Completions endpoint, MyMemory and Ollama are supported. See [provider setup](docs/AI_PLATFORMS.md). Presets are not shared keys or a promise of free API access. Ollama translation models are optional separate downloads; OCR models are included.

Screenshots stay on-device. Recognized text is sent to the chosen translation service. Keys are encrypted for the current Windows user and isolated by provider/endpoint. Settings, vocabulary and logs live in `%LOCALAPPDATA%\LexiLens`.

## Verification

80 core tests pass. Desktop diagnostics cover actual capture, isolated OCR, live MyMemory translation, window snapping, click-through, deliberate worker stalls/cancellation, fallback and diagnostic export. Native inference is bounded by a 15-second initial / 8-second later timeout. Missing VC++ x64 runtimes can be addressed through the Microsoft link in Settings.

The v0.3.0 wait condition was reproduced: alternating OCR results produced zero API calls; the fix produced one. This identifies a code defect, not proof that every user's waiting screen has the same cause. See [bug analysis](docs/BUG_ANALYSIS.md), [project research](docs/RESEARCH.md) and [test records](docs/TESTING.md).

Automatic semantic grouping inside a whole-window selection is not implemented; this release uses manually selected areas. Very brief unsampled frames can still be missed, and remote generation takes additional time. See [performance boundaries](docs/PERFORMANCE.md).

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
