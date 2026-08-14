# Phase 12 — Checklist Premiere 24/30 fps

Tài liệu này chỉ dùng cho hai candidate Phase 12 đã khóa hash. Không import XML nguồn `*-input.xml`, không dùng lại PCM/XML re-export của phase cũ và không ghi đè bất kỳ file nào.

## Candidate 24 fps

- Import file:
  `F:\RIN APP\App-Auto-Edit_codex_2\private-artifacts\phase12-premiere-fixture-24-20260814-1\PHASE12_24FPS_NDF_ADOPTION_AutoAudio_20260814-042757-bcf1154b8b3048849515a443953bdbe4\PHASE12_24FPS_NDF_ADOPTION_AutoAudio.xml`
- SHA-256 bắt buộc: `69322FF3D8CD335C99A9505AEA84002F0C19D499CB95941179D61EB4851316D2`.
- Thư mục xuất bằng chứng mới:
  `F:\RIN APP\App-Auto-Edit_codex_2\private-artifacts\phase12-premiere-roundtrip-24-20260814-1`
- Xuất track A1 thành WAV mono PCM `48 kHz`, `24-bit`, bắt đầu đúng từ đầu sequence, tên `24fps-A1.wav`.
- Export Final Cut Pro XML của đúng sequence vừa import, tên `24fps-premiere-reexport.xml`.

## Candidate 30 fps

- Import file:
  `F:\RIN APP\App-Auto-Edit_codex_2\private-artifacts\phase12-premiere-fixture-30-20260814-1\PHASE12_30FPS_NDF_ADOPTION_AutoAudio_20260814-042758-8bb70f799370403092ca1b9ebd82cb36\PHASE12_30FPS_NDF_ADOPTION_AutoAudio.xml`
- SHA-256 bắt buộc: `F5929453819147F8D201EC0F5B942CCA42E02867D4CA90E9712EB31A0045FEC0`.
- Thư mục xuất bằng chứng mới:
  `F:\RIN APP\App-Auto-Edit_codex_2\private-artifacts\phase12-premiere-roundtrip-30-20260814-1`
- Xuất track A1 thành WAV mono PCM `48 kHz`, `24-bit`, bắt đầu đúng từ đầu sequence, tên `30fps-A1.wav`.
- Export Final Cut Pro XML của đúng sequence vừa import, tên `30fps-premiere-reexport.xml`.

## Kiểm tra trực quan cho mỗi sequence

1. Sequence phải hiện đúng `24,00 fps` hoặc `30,00 fps`; duration là `13 giây`.
2. A1 có ba source clip: hai clip đầu liền nhau tại giây 5, sau đó có gap đúng một giây từ giây 8 đến giây 9.
3. Lời qua biên giây 5 phải nghe liên tục; vùng silence cuối clip thứ hai và cuối sequence phải Disabled.
4. Có bốn marker, gồm một marker `Gain đã giới hạn +18 dB` ở vùng lời rất nhỏ.
5. Không relink sang WAV khác. Media đúng là `phase12-24fps-ndf-voice.wav` hoặc `phase12-30fps-ndf-voice.wav`, cùng SHA-256 `7F54C0FA61789A5F97791D7DA0B5A564BD8822FEBC57C770E7A0F682AA518F96`.

Sau khi đủ WAV và XML re-export, chạy `scripts/phase12/Test-IntegerNdfPremiereAdoption.ps1`. Validator tự khóa source/candidate/audit hash, timing NDF, peak phrase, target hậu routing, Disabled, gain và marker; mỗi report là file mới và không được ghi đè. Report chỉ kết luận các artifact tương thích; nó không tự chứng minh ứng dụng đã tạo file, nên vẫn phải đi cùng xác nhận import/phát trực quan ở checklist trên.
