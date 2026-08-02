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

## Timeline, câu thoại và gain

- Audio 48 kHz được lấy mỗi mẫu thứ ba thành 16 kHz đúng như wrapper chính thức của Silero; mỗi block VAD tương ứng 32 ms timeline.
- State VAD được giữ qua ranh giới hai WAV khi clip nối liền nhau. Gap từ 350 ms trở lên flush block còn lại và reset state; gap ngắn hơn được đưa vào detector dưới dạng silence.
- Source dài hơn timeline do làm tròn dưới một frame được crop theo timeline; source ngắn hơn được pad silence. Chênh lệch lớn hơn một frame đã bị Phase 02 từ chối.
- Noise floor dùng cửa sổ thích nghi 512 block non-speech và percentile 20; direct speech phải vừa đạt VAD 0.50 vừa cao hơn floor ít nhất 10 dB trong tối thiểu 120 ms.
- Các block VAD mạnh nhưng quá ngắn/yếu, cùng block sát threshold có năng lượng giọng, được giữ là `Ambiguous`; không bị Disable.
- Các cụm speech cách nhau dưới 350 ms dùng một quyết định gain. Padding 200/300 ms không được tính vào peak; padding hai câu gần nhau được chặn tại midpoint để không có hai gain chồng nhau.
- Peak được đo lại từ đúng source range của những block direct speech đã xác nhận. Gain yêu cầu là `-6 - peak dBFS`; boost trên +18 dB bị cap và phrase mang cờ audit.
- Một phrase đi qua hai WAV vẫn có một ID/gain, nhưng segment giữ nguyên từng clip/source range để Phase 04 có thể viết XML không đổi media reference.

## Bleed và bằng chứng xung đột

Một segment chỉ được coi là bleed khi đồng thời:

- mic khác có direct phrase chồng ít nhất 120 ms và lớn hơn mic đích ít nhất 12 dB;
- waveform correlation tuyệt đối từ 0.80 trong cửa sổ tối đa 1 giây;
- lag tốt nhất không quá 12 ms;
- RMS mic đích thấp hơn mẫu direct voice trung vị đã học trên chính track đó.

Correlation và phép khớp residual chạy trên waveform thật đã lấy mỗi mẫu thứ ba, không dùng chỉ số VAD thay waveform. Nếu phần residual độc lập còn lớn hơn `-10 dB` so với tín hiệu mic đích, bằng chứng được xem là xung đột và segment giữ `Ambiguous`. Buffer so sánh được tái sử dụng; frame observation bị giải phóng sau khi tạo segment để không tăng RAM theo toàn show.

## Điều phối và hủy

- Tối đa bốn track được phân tích song song; mỗi detector dùng một ONNX thread.
- Mỗi vòng đọc/scan/peak/correlation kiểm tra cancellation. Hủy trước khi bắt đầu không mở media.
- Phase 03 không có API ghi XML/audit/output; tool nội bộ `--analyze` chỉ in thống kê làm sạch ra console.

## Bằng chứng kiểm thử

Ngày 2026-08-02 trên Windows:

- Release build: đạt, `0` warning và `0` error.
- MSTest: `49/49` đạt.
- Model nhúng khớp đúng dung lượng `2,327,524` byte và SHA-256 đã ghim.
- Inference silence cho xác suất hữu hạn thấp hơn `0.10`; reset state tái lập đúng kết quả khối đầu.
- Fixture PCM xác nhận biên âm/dương 16-bit, sign extension 24-bit, valid 24-bit trong container 32-bit và source trim không bắt đầu từ 0.
- Fixture timeline xác nhận decimation 48→16 kHz, state liên tục qua hai WAV, reset qua gap, phrase/gain qua clip boundary, padding, gain cap, ambiguous và bleed correlation/residual.
- Test điều phối xác nhận không vượt bốn worker; token đã cancel dừng trước khi mở media.

Kiểm tra read-only trên media thật:

- `test HGE2.xml`: 7 track/7 WAV, hoàn tất khoảng `30.1 s`, peak RAM `194.7 MB`; 2.132 phrase. Không tạo output.
- `test HGE.xml`: 7 track/19 WAV, timeline 13 giờ 17 phút, hoàn tất khoảng `12 phút 37 giây`, peak RAM `719.4 MB`; 43.382 phrase. Chấp nhận đúng cảnh báo 1 byte PCM tail và các sai lệch làm tròn đã được Phase 02 kiểm tra. Không tạo output.
- Lượt full đầu tiên bị dừng bằng RAM guard khi peak đạt khoảng `2.23 GB`. Sau khi bỏ frame observation khỏi kết quả và tái sử dụng buffer correlation, cùng input hoàn tất ở `719.4 MB`.

Hai lượt media thật không tìm thấy segment nào đồng thời đạt đủ mọi điều kiện bleed. App giữ các vùng chưa đủ bằng chứng; kết quả này **không** chứng minh tiêu chí nghiệm thu 90% noise/bleed hoặc 100% direct speech. Việc đó vẫn cần bộ nhãn thủ công HGE2 ở Phase 06. Phase 03 cũng chưa tạo XML; writer thuộc Phase 04.

Các phần segment speech/noise, noise floor thích nghi, peak/gain, bleed và điều phối tối đa bốn worker sẽ được ghi tiếp vào tài liệu này trước khi Phase 03 được merge.
