# Kết quả Phase 00 — Premiere Pro 2026

Ngày kiểm tra: 2026-08-02
Trạng thái cổng: **ĐẠT — được phép bắt đầu Phase 01**

## Phạm vi kiểm tra

- Premiere Pro 2026 trên Windows.
- FCP XML 25 fps, stereo, WAV nguồn mono PCM 48 kHz 24-bit.
- Clip Disable, timeline range, source `in/out` khác 0 và `pproTicksIn/Out`.
- Gain tĩnh từ giảm mức đến `+18 dB`.
- PCM export 48 kHz stereo để đối chiếu mức nghe thực tế, không chỉ đọc UI hoặc XML.

## Kết quả cơ sở

- `-6`, `+6` và `+12 dB` bằng một Audio Levels: đạt trong `±0.1 dB`.
- `enabled=FALSE`: được giữ khi round-trip và PCM không cao hơn `-90 dBFS`.
- Duration, số fragment, timeline, source trim và ticks: không đổi.
- Hai Audio Levels cùng loại được đặt trực tiếp trong XML không cộng dồn ổn định; Premiere chỉ giữ effect cuối.
- Một Audio Levels lớn hơn `+12 dB` có thể render khoảng `+15 dB` nhưng re-export về `+12 dB`, nên không phải encoding trung thực.

## Phát hiện về Gain filter

Audio Gain tạo thủ công trong Premiere được export thành filter:

- `effectid`: `{61756678, 4761696e, 4b657947}`;
- tham số: `Gain(dB)`;
- `valuemin=-96`, `valuemax=96`.

Tên tham số gây hiểu nhầm khi import XML: Premiere diễn giải `<value>` như hệ số tuyến tính, không phải số dB. Ví dụ literal `3` được render thành `20 log10(3) = +9.542 dB`.

Vì vậy app phải ghi:

```text
gainFilterValue = 10^(gainDb / 20)
```

## Encoding đã được chấp nhận

- Gain không lớn hơn `+12 dB`: dùng một Audio Levels với `value=10^(gainDb/20)`.
- Gain lớn hơn `+12 dB` đến tối đa `+18 dB`:
  1. Audio Levels thứ nhất cố định `+12 dB`, `value=3.981071706`.
  2. Thêm Premiere Gain filter cho phần còn lại `remainderDb = gainDb - 12`.
  3. Ghi `Gain(dB)/value = 10^(remainderDb/20)`, không ghi literal dB.

Khi Premiere import, Gain filter được chuyển thành Audio Levels thứ hai. Re-export giữ cả hai Audio Levels và PCM áp tổng gain đúng.

## Bằng chứng vòng quyết định

| Ca | XML re-export | PCM relative | Kết quả |
|---|---:|---:|---|
| Unity | `0 dB` | `0 dB` | Đạt |
| Audio Levels `+12 dB` | `+12 dB` | `+12 dB` | Đạt |
| Gain factor `+3 dB` | `+3 dB` | `+3 dB` | Đạt |
| Gain factor `+6 dB` | `+6 dB` | `+6 dB` | Đạt |
| Gain factor `+12 dB` | `+12 dB` | `+12 dB` | Đạt |
| Level `+12` + Gain factor `+6` | `+18 dB` | `+18 dB` | **Đạt** |
| Level `+6` + Gain factor `+12` | `+18 dB` | `+18 dB` | **Đạt** |

WAV round-trip dài đúng 14 giây, PCM signed 16-bit, 48 kHz stereo. Tất cả timing, source range và `pproTicksIn/Out` qua validator không đổi.

SHA-256 bằng chứng cục bộ không commit:

- Input XML: `6A1ED66D814758D15A463F93A2D25F31D3BF786BDCA048D09D44D1BD5787A749`
- Input WAV: `E8FF1CCA69E5572F72AC56321EC5710A1A3A3EA2BE79EB73496C927945AD46DB`
- Premiere XML: `6058CA0ED618F6324CCFFCDB959C198ECB74938E23FC1927E5761978BCB835F5`
- Premiere WAV: `78ACF9C80329EA5EACF8626D93E24E92C5027DEF29EF168E2A37EE8BEF27B650`

Fixture đã làm sạch: `tests/fixtures/phase00/premiere-2026-linear-gain-roundtrip-sanitized.xml`.

## Quyết định

Phase 00 đạt cổng tương thích với sai số `±0.1 dB`. Phase 01 được mở với điều kiện implementation dùng đúng encoding ở trên và giữ các giới hạn đã khóa:

- boost tối đa `+18 dB`;
- không ghi literal dB vào Premiere Gain-filter value;
- không dựa vào hai Audio Levels cùng loại trong XML đầu vào để tạo gain cao;
- không suy rộng kết quả sample peak thành LUFS, true peak hoặc Master-bus guarantee;
- media và project Premiere gốc luôn bất biến.
