# Dependencies

Downloaded 2026-10-14 from upstream distributors. Version 0.4.0 uses OpenAI whisper-1 API by default; audio is uploaded for cloud transcription after recording or manual reprocessing. The optional local mode uses the bundled dependencies below without uploading audio. The English release retains the same pinned engine and models.

| Component | Version | Source |
|---|---|---|
| NAudio.Core / NAudio.Wasapi | 2.2.1 | https://www.nuget.org/packages/NAudio.Core/2.2.1 and https://www.nuget.org/packages/NAudio.Wasapi/2.2.1 |
| whisper.cpp Windows x64 CPU | b5130 | https://github.com/ggml-org/whisper.cpp/releases/tag/b5130 |
| Whisper base multilingual | ggml-base.bin | https://huggingface.co/ggerganov/whisper.cpp/blob/main/ggml-base.bin |
| Silero VAD | v6.2.0 GGML | https://huggingface.co/ggml-org/whisper-vad/blob/main/ggml-silero-v6.2.0.bin |

Whisper model SHA-256 (verified against Hugging Face LFS metadata):

`60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`

Silero VAD local SHA-256:

`2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987`

NAudio and whisper.cpp are MIT licensed. Whisper model and Silero license texts are included. Only required whisper CLI and GGML runtime files are distributed. Per-file SHA-256 manifest: `SHA256SUMS.txt`.
