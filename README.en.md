# LexiLens

A Windows screen translation tool with a bilingual overlay and an English vocabulary list.

[Download](https://github.com/observer233/LexiLens/releases/latest) · [中文](README.md) · [Setup](docs/USER_GUIDE.md) · [Bug analysis](docs/BUG_ANALYSIS.md)

![LexiLens v0.6.0](docs/images/main.png)

## v0.6.0

- **In-game selection:** Ctrl+Alt+Q draws a temporary area and starts translation on release. The main window stays minimized; the previous foreground window is restored when possible. Escape cancels without replacing existing selections.
- **Temporary or fixed:** Temporary areas follow text continuously by default, with an option for a single snapshot. Ctrl+Alt+A adds a fixed area; R replaces one and W selects a window. Fixed selection automatically starts continuous translation.
- **Positioned translation:** OCR word/line coordinates are retained. Paragraphs are grouped by baseline, spacing and font size, with individual identities and translations.
- **Three views:** Translucent in-place replacement, nearby paragraph cards, and the original combined floating overlay. Ctrl+Alt+V cycles views.
- **Ownership and freshness:** Smaller overlapping fixed areas own their text centers. A temporary selection takes priority until cleared. Disappeared or stale text is removed; late obsolete replies go to replay without covering replacement text.

## Use

1. Extract the Windows x64 ZIP and run `LexiLens.exe`. .NET and OCR models are included. Configure and test your translation service.
2. Return to the game. Press **Ctrl+Alt+Q**, drag a region, release and translate. **X** clears it; **M** opens the main window.
3. Use **Ctrl+Alt+A** for a persistent region, **R** to replace it, or **W** to select a whole window. Each region can contain multiple automatically located paragraphs.
4. Use **V** to change display, **T** to start/stop, **F** for a fixed-area one-shot, **H** to hide/show and **L** to unlock/click-through (all with Ctrl+Alt).

Temporary continuous follow can be disabled for snapshots. Fixed regions retain the previous four-area limit and survive app restarts. Selections do not track window moves or resizing. Exclusive fullscreen games may lose focus or pause during an external picker; desktop focus restoration is tested, individual games remain unverified.

## Recognition and providers

Fast positioned mode uses Windows OCR and clears empty frames immediately; the combined view retains enhanced fallback on empty results. Disable fast mode for stylized fonts. English/Chinese enhanced recognition uses bundled CPU PaddleOCR v5; Windows OCR is also available and handles other installed languages. Stylized or obscured text can still be misread. Recognition inspection displays the crop and raw OCR without calling a translation API.

25 cloud presets, a custom Chat Completions endpoint, MyMemory and Ollama are supported. See [provider setup](docs/AI_PLATFORMS.md). Presets are not shared keys or a promise of free API access. Ollama translation models are optional separate downloads; OCR models are included.

Screenshots stay on-device. Recognized text is sent to the chosen translation service. Keys are encrypted for the current Windows user and isolated by provider/endpoint. Settings, vocabulary and logs live in `%LOCALAPPDATA%\LexiLens`.

## Verification

90 core tests pass. Desktop diagnostics cover actual capture, isolated OCR, live MyMemory translation, window snapping, click-through, deliberate worker stalls/cancellation, fallback and diagnostic export. Native inference is bounded by a 15-second initial / 8-second later timeout. Missing VC++ x64 runtimes can be addressed through the Microsoft link in Settings.

The v0.3.0 wait condition was reproduced: alternating OCR results produced zero API calls; the fix produced one. This identifies a code defect, not proof that every user's waiting screen has the same cause. See [bug analysis](docs/BUG_ANALYSIS.md), [project research](docs/RESEARCH.md) and [test records](docs/TESTING.md).

Automatic grouping is geometric, not semantic classification. The default limit is 12 paragraphs per area (configurable from 4 to 24); dense layouts or stylized fonts can still be grouped incorrectly. Long translations may be clipped in small original bounds; replay retains full text. Very brief unsampled frames can still be missed, and remote generation takes additional time. See [performance boundaries](docs/PERFORMANCE.md).

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
