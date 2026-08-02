# Phase 00 mở rộng — Premiere Gain filter

## Kết quả phép thử thủ công `+3 dB`

Premiere Pro 2026 xuất Audio Gain thành một filter độc lập với clip volume:

- tên effect: `Gain`;
- `effectid`: `{61756678, 4761696e, 4b657947}`;
- tham số: `Gain(dB)`;
- giá trị thử nghiệm: `3`;
- `Audio Levels` của cùng clip vẫn giữ hệ số `1`, tương đương `0 dB`.

So với WAV round-trip trước đó, chỉ clip đầu thay đổi:

| Đoạn | Peak trước | Peak sau | Chênh lệch |
|---|---:|---:|---:|
| Clip đầu, Audio Gain `+3 dB` | `-51.1 dBFS` | `-48.1 dBFS` | `+3.0 dB` |
| Sáu clip đối chứng | không đổi | không đổi | `0.0 dB` |

Kết quả của `Test-ManualAudioGainExport.ps1` là `manual-audio-gain-compatible`. Điều này chứng minh Audio Gain thủ công được mã hóa độc lập và tác động đúng lên PCM trong lần export hiện tại. Nó chưa đủ để mở cổng Phase 00: XML do ứng dụng sinh ra vẫn phải được Premiere import, re-export và render PCM thêm một vòng.

SHA-256 của bằng chứng cục bộ không commit:

- Premiere XML: `D9C6F95343F8ED52F563362548E985C265E0DAFB01987DB8A8C82925E90A4012`
- Premiere WAV: `A655FC5DCA8CD2457DA557781AE4B4349456CFE5348825E497E665CA09089619`

## Ma trận round-trip Gain filter

Fixture tiếp theo dùng đúng filter mà Premiere vừa xuất và kiểm tra bảy đoạn, mỗi đoạn hai giây:

| Ca | Audio Levels | Gain filter | Tổng mong đợi |
|---|---:|---:|---:|
| Unity reference | `0 dB` | `0 dB` | `0 dB` |
| Level reference | `+12 dB` | `0 dB` | `+12 dB` |
| Gain reference | `0 dB` | `+3 dB` | `+3 dB` |
| Gain-only candidate | `0 dB` | `+18 dB` | `+18 dB` |
| Gain-only observation | `0 dB` | `+15 dB` | `+15 dB` |
| Split candidate A | `+12 dB` | `+6 dB` | `+18 dB` |
| Split candidate B | `+6 dB` | `+12 dB` | `+18 dB` |

Một candidate chỉ đạt nếu tổng gain trong XML re-export và PCM relative gain đều là `+18.0 ±0.1 dB`, đồng thời timing, source trim và `pproTicksIn/Out` không đổi. Chỉ cần một candidate đạt là đủ chọn encoding kỹ thuật; ưu tiên Gain-only nếu nhiều candidate cùng đạt vì chỉ có một trường gain cần ghi.

Fixture cục bộ đã được tạo và kiểm tra cấu trúc thành công:

- XML: `private-artifacts/phase00/3/phase00-gain-filter-input.xml`
- WAV: `private-artifacts/phase00/3/phase00-gain-filter-tone.wav`
- kết quả preflight: `gain-filter-input-valid`
- SHA-256 XML: `B027A2865275D80989AA7EC854DFED3FDC9DA452FE74CDF67B4031D2B332ABAA`
- SHA-256 WAV: `E8FF1CCA69E5572F72AC56321EC5710A1A3A3EA2BE79EB73496C927945AD46DB`

Trạng thái hiện tại: Phase 00 vẫn đang chờ Premiere round-trip của fixture Gain filter.
