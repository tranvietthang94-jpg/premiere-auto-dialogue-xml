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
- Lỗi/hủy xóa file tạm và không để XML kết quả một phần.
- Audit chứa SHA-256 input/output, model/checksum/preset, source/timeline range, trạng thái, peak/gain, bleed evidence và marker reason; không chứa sample audio.

Tài liệu sẽ được bổ sung bằng test/evidence trước khi Phase 04 được merge.
