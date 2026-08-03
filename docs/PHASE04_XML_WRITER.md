# Phase 04 — XML writer và audit giao dịch

## Phạm vi

Phase 04 nhận `PremiereProject` đã kiểm tra cùng `ProjectAudioAnalysis` có cùng provenance, clone XML nguồn và chỉ thay sequence/audio clipitems trong bản sao. Video, sequence settings, track attributes/routing, media metadata và path URL phải giữ nguyên.

Mỗi source clip được thay bằng các fragment nối tiếp theo quyết định Phase 03:

- `Speech`: `enabled=TRUE`, Audio Levels tĩnh theo gain phrase.
- `Noise`/`Bleed`: `enabled=FALSE`; fragment vẫn tồn tại để editor bật lại.
- `Ambiguous`: `enabled=TRUE`, marker “Cần kiểm tra”; nếu sát phrase thì kế thừa gain, nếu độc lập dùng unity.

Fragment cập nhật đồng bộ `start/end`, `in/out`, `pproTicksIn/Out`, giữ `sourcetrack`, `file` và metadata clip gốc. Một phrase qua hai WAV vẫn giữ cùng phrase ID/gain nhưng dùng hai fragment/media reference.

## Encoding gain đã khóa bởi Phase 00

- Gain không lớn hơn `+12 dB`: một Audio Levels, `value=10^(gainDb/20)`.
- Gain trên `+12` đến `+18 dB`: Audio Levels cố định `+12 dB` (`3.981071706`) và Premiere Gain filter cho phần còn lại, trong đó `Gain(dB)/value=10^(remainderDb/20)`.
- Gain giảm dùng một Audio Levels với hệ số nhỏ hơn 1.
- Không ghi hai Audio Levels cùng loại để cộng boost; không ghi literal dB vào `Gain(dB)/value`.

## An toàn file

- Mỗi run dùng thư mục mới; không ghi đè input, thư mục run hoặc file kết quả đã có.
- XML/audit được ghi vào file tạm trong chính thư mục run, flush xong mới rename về tên cuối.
- XML vừa ghi được đọc lại, kiểm SHA-256 và so độc lập với XML nguồn: video, settings, routing, media path, fragment timing, source trim, enabled, gain effect, ID và marker.
- Audit vừa ghi cũng được deserialize và so toàn bộ field/fragment/marker với dữ liệu trong bộ nhớ trước khi công bố file cuối.
- Lỗi/hủy xóa file tạm và không để XML kết quả một phần.
- Audit chứa SHA-256 input/output, model/checksum/preset, source/timeline range, trạng thái, peak/gain, bleed evidence và marker reason; không chứa sample audio.

## Kiểm thử và bằng chứng

- `dotnet format --verify-no-changes`: đạt.
- `dotnet build -c Release`: đạt, 0 warning/0 error.
- `dotnet test -c Release`: 73/73 test đạt, gồm XML contract, gain encoding, frame alignment, ID uniqueness, source trim, marker, stale hash, cancellation/collision, audit transaction, Unicode/tên Windows và provenance analysis.
- Chạy thật với fixture HGE2 ngày 2026-08-03:
  - XML nguồn giữ nguyên SHA-256 `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`.
  - Thời gian toàn chuỗi 30,53 giây; peak working set khi phân tích 182,6 MB.
  - Output `AN TRƯƠNG_AutoAudio.xml`: 11.282.996 byte, SHA-256 `524823BE6595BA1F9E1E0C55D159DC4C2487F02D6B3F535588884C787B0120F5`.
  - Audit: 7.240.752 byte; hash input/output trong audit khớp file thật.
  - 8.058 audio fragment và 2.956 marker; model Silero VAD 6.2.1 đúng checksum đã ghim.
  - 7 warning chỉ là XML khai 16-bit trong khi WAV header là 24-bit; writer dùng WAV header theo hợp đồng và không sửa media.
  - Bộ kiểm tra post-write đã xác nhận video, sequence duration/settings, 7 track/routing, media reference, source/timeline continuity và quyết định enabled/gain không bị lệch.

XML/audit chạy thật nằm trong `private-artifacts` bị Git ignore và không được commit. Bước import/nghe trong Premiere thuộc cổng pilot thực tế; bằng chứng cấu trúc ở đây không được diễn giải thành bảo đảm Master bus, LUFS hoặc true peak.
