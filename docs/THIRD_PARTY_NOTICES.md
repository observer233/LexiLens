# Third-party components

LexiLens application code is MIT licensed. Bundled third-party libraries, native binaries and model weights retain their own licenses; the application's MIT license does not replace them. License texts are included in the portable release under `docs/licenses/`.

| Component | Version / origin | License |
| --- | --- | --- |
| .NET / WPF | .NET 10, Microsoft | `.NET-LICENSE.txt` and `.NET-ThirdPartyNotices.txt` at the package root |
| [PaddleSharp / Sdcb.PaddleOCR](https://github.com/sdcb/PaddleSharp) | 3.3.1 | [Apache 2.0](licenses/Apache-2.0.txt) |
| PaddleOCR local v5 models | Sdcb.PaddleOCR.Models.Local 3.3.1 / LocalV5 3.0.0 | [Apache 2.0](licenses/Apache-2.0.txt), upstream [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) |
| [PaddlePaddle / Paddle Inference](https://github.com/PaddlePaddle/Paddle) | Native runtime 3.3.1.70 | [Apache 2.0 / Paddle copyright](licenses/PaddlePaddle-Apache.txt) |
| [OpenCvSharp](https://github.com/shimat/opencvsharp) | 4.11.0.20250507 | [Apache 2.0](licenses/OpenCvSharp-Apache.txt) |
| [OpenCV](https://github.com/opencv/opencv) | 4.11 | [Apache 2.0](licenses/OpenCV-Apache.txt) |
| [oneDNN](https://github.com/oneapi-src/oneDNN) | Runtime reports 3.6.2 | [Apache 2.0](licenses/oneDNN-Apache.txt) and [third-party notices](licenses/oneDNN-ThirdParty.txt) |
| Intel MKLML / Intel OpenMP CPU runtime | Native binaries supplied by the Sdcb MKL runtime package | [Intel Simplified Software License](licenses/Intel-MKLML-license.txt), reproduced from Intel's MKLML Windows distribution |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | Dependency included by the Paddle runtime | [MIT](licenses/ONNXRuntime-MIT.txt) |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | 16.3.0 | [MIT](licenses/YamlDotNet-MIT.txt) |

The unused OpenCV FFmpeg video plugin is removed from single-file release bundles; LexiLens only needs still-image OCR. Cloud APIs and optional Ollama translation models are external services/downloads governed by their providers' terms. LexiLens does not distribute cloud credentials.

Source and binary provenance is recorded by the pinned NuGet package references in `src/LexiLens.App/LexiLens.App.csproj`. NuGet metadata identifies licenses, repositories and, where supplied by upstream, source commits. Dependencies are used without source modifications; LexiLens implements its own image preparation, capture and OCR layout handling.
