# Kết quả Phase 00 — Premiere Pro 2026

Ngày kiểm tra: 2026-08-02
Trạng thái cổng: **KHÔNG ĐẠT — dừng trước Phase 01**

## Phạm vi kiểm tra

- Premiere Pro 2026 trên Windows.
- Sequence 25 fps, stereo, dài 10 giây.
- Năm fragment hai giây: unity, `+6 dB`, `+12 dB`, `+18 dB` bằng hai Audio Levels `+9 dB`, và Disabled.
- Mỗi fragment dùng một source trim khác nhau trên cùng WAV tổng hợp 48 kHz mono PCM 24-bit.
- XML được import vào project thử riêng, re-export thành FCP XML và render toàn sequence thành WAV PCM 48 kHz stereo.

## Bằng chứng

| Ca | XML sau round-trip | Peak PCM | Kết luận |
|---|---:|---:|---|
| Unity | `0 dB` | `-45.1 dBFS` | Đạt |
| `+6 dB` | `+6 dB` | `-39.1 dBFS` | Đạt, lệch tương đối `+6.0 dB` |
| `+12 dB` | `+12 dB` | `-33.1 dBFS` | Đạt, lệch tương đối `+12.0 dB` |
| Hai Audio Levels `+9 +9 dB` | Chỉ còn một Audio Level `+9 dB` | `-36.1 dBFS` | **Không đạt**, cần `+18 dB` nhưng chỉ có `+9 dB` |
| Disabled | `enabled=FALSE` | `-91.0 dBFS` | Đạt ngưỡng không cao hơn `-90 dBFS` |

Premiere giữ đủ năm fragment, sequence duration 250 frame, timeline range, source `in/out`, `pproTicksIn/Out`, track count và trạng thái Disabled. Lỗi duy nhất nhưng có tính chặn là hai Audio Levels không cộng dồn: Premiere chuẩn hóa chúng thành một effect `+9 dB`, và PCM xác nhận thiếu đúng `9.0 dB`.

Peak tuyệt đối của tone không phải mục tiêu kiểm tra; fixture dùng tone FFmpeg đã giảm mức và Premiere pan mono vào stereo. Cổng dùng chênh lệch tương đối so với fragment unity.

SHA-256 của bằng chứng cục bộ không commit:

- Input XML: `9953C51A7B50D4DCD1742F9050E33312E4E1A0DB75FAA6FA9CD73C47A5433257`
- Premiere XML: `D4D2000FAE2DA906B5263A7C6D8A4F2F43CF1488B403308300B620B455E84909`
- Premiere WAV: `562E8126A36686FE86D9F6DC8104EBC7CC5739D6DC28890E6147B9B4003A7BE4`

## Quyết định

Theo điều kiện đã khóa, sai số `9.0 dB` lớn hơn giới hạn `0.1 dB`, nên không mở Phase 01 và không âm thầm:

- cap gain ở `+12 dB`;
- thay XML bằng WAV đã render;
- tuyên bố mức `+18 dB` hoạt động.

Fixture đã làm sạch nằm tại `tests/fixtures/phase00/premiere-2026-roundtrip-sanitized.xml`. WAV, project Premiere và output thật vẫn chỉ nằm cục bộ, bị Git bỏ qua.
