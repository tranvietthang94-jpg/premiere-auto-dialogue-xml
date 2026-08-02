# Phase 03 — phân tích âm thanh

## Hợp đồng model đã khóa

- Silero VAD `v6.2.1`, commit `7e30209a3e901f9842f81b225f3e93d8199902b1`.
- File nhúng `silero_vad.onnx`, SHA-256 `1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3`.
- Model và mã nguồn Silero VAD dùng giấy phép MIT; license đầy đủ nằm cạnh model.
- Microsoft.ML.OnnxRuntime `1.28.0`, CPU, một thread cho mỗi detector.
- Toàn bộ inference chạy cục bộ; app không gửi audio, telemetry hoặc request ra Internet.
- Mỗi lần khởi tạo đều kiểm tra SHA-256 của model nhúng trước khi tạo inference session.

Wrapper bám đúng hợp đồng chính thức của model 16 kHz: mỗi khối có 512 mẫu mới, 64 mẫu ngữ cảnh và state `2 × 1 × 128`. Input/output tensor được tạo một lần và dùng lại để giữ mức cấp phát ổn định trên timeline dài.

## Bộ đọc PCM

- Chỉ mở source ở chế độ đọc và seek bằng offset 64-bit tới đúng `source in`.
- Chỉ đọc số PCM frame mà clip sử dụng; không quét phần đầu/cuối WAV nằm ngoài source trim.
- Giải mã signed little-endian PCM 16/24/32-bit về `float` trong khoảng `[-1, 1)`.
- Với WAVE_FORMAT_EXTENSIBLE, valid bits được lấy theo header và xử lý theo quy tắc left-aligned trong container.
- Buffer byte/float được thuê lại từ pool, xử lý theo khối giới hạn và kiểm tra hủy giữa các khối.

## Bằng chứng mốc đầu tiên

Ngày 2026-08-02 trên Windows:

- Release build: đạt, `0` warning và `0` error.
- MSTest: `36/36` đạt.
- Model nhúng khớp đúng dung lượng `2,327,524` byte và SHA-256 đã ghim.
- Inference silence cho xác suất hữu hạn thấp hơn `0.10`; reset state tái lập đúng kết quả khối đầu.
- Fixture PCM xác nhận biên âm/dương 16-bit, sign extension 24-bit, valid 24-bit trong container 32-bit và source trim không bắt đầu từ 0.

Các phần segment speech/noise, noise floor thích nghi, peak/gain, bleed và điều phối tối đa bốn worker sẽ được ghi tiếp vào tài liệu này trước khi Phase 03 được merge.
