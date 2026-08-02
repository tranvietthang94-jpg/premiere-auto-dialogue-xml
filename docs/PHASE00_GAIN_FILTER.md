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

## Kết quả round-trip giá trị dB literal

Vòng thử trong `private-artifacts/phase00/3/result` không đạt. Premiere không diễn giải giá trị của tham số XML `Gain(dB)` theo đơn vị dB khi import; nó diễn giải giá trị đó như hệ số tuyến tính:

| Input Gain XML | Gain PCM quan sát | XML re-export |
|---:|---:|---:|
| `3` | `+9.5 dB` (`20 log10(3)`) | Audio Levels `+9.542 dB` |
| `15` | `+15 dB`, bị clamp | Audio Levels `+12 dB` |
| `18` | `+15 dB`, bị clamp | Audio Levels `+12 dB` |
| Level `+12 dB` + Gain `6` | `+27 dB` | hai Audio Levels tổng `+24 dB` |
| Level `+6 dB` + Gain `12` | `+21 dB` | hai Audio Levels tổng `+18 dB` |

Không candidate nào đạt đồng thời XML và PCM. Kết quả preflight cũ `gain-filter-input-valid` chỉ phản ánh giả định tên trường là dB; validator đã được sửa để mô hình hóa đúng hành vi importer là `20 log10(value)`.

SHA-256 của output Premiere cục bộ:

- XML: `6A7ADFDF8277590ED55EAB104413C375EA9F15067ADC9CE718591296FAE1B85C`
- WAV: `53068816B3388E90CFD7896BD558A6801B4511DDA4A90C01A781EE3C210B3218`

## Fixture hệ số tuyến tính

Vòng kế tiếp ghi `Gain(dB)` bằng hệ số `10^(dB/20)`, đúng với hành vi importer vừa đo:

- `+3 dB` → `1.412537545`;
- `+6 dB` → `1.995262315`;
- `+12 dB` → `3.981071706`;
- `+18 dB` → một Audio Levels `+12 dB` kết hợp Gain-filter factor `+6 dB`, hoặc Audio Levels `+6 dB` kết hợp Gain-filter factor `+12 dB`.

Validator yêu cầu cả năm mốc `0/+3/+6/+12 dB` và ít nhất một candidate `+18 dB` đạt XML lẫn PCM trong `±0.1 dB`; timing, source trim và `pproTicksIn/Out` vẫn phải nguyên vẹn.

Fixture đã đạt preflight `gain-filter-input-valid`:

- XML: `private-artifacts/phase00/4/phase00-linear-gain-input.xml`
- WAV: `private-artifacts/phase00/4/phase00-linear-gain-tone.wav`
- SHA-256 XML: `6A1ED66D814758D15A463F93A2D25F31D3BF786BDCA048D09D44D1BD5787A749`
- SHA-256 WAV: `E8FF1CCA69E5572F72AC56321EC5710A1A3A3EA2BE79EB73496C927945AD46DB`

## Kết quả hệ số tuyến tính

Cả hai candidate `+18 dB` đều đạt:

- Level `+12 dB` + Gain-factor `+6 dB`: XML `+18 dB`, PCM `+18.0 dB`;
- Level `+6 dB` + Gain-factor `+12 dB`: XML `+18 dB`, PCM `+18.0 dB`.

Các mốc `0`, `+3`, `+6` và `+12 dB` cũng đạt XML và PCM trong `±0.1 dB`; timing và source trim không đổi. SHA-256 output Premiere:

- XML: `6058CA0ED618F6324CCFFCDB959C198ECB74938E23FC1927E5761978BCB835F5`
- WAV: `78ACF9C80329EA5EACF8626D93E24E92C5027DEF29EF168E2A37EE8BEF27B650`

Trạng thái cuối: **Phase 00 đạt**. Implementation chọn Audio Levels `+12 dB` kết hợp Gain-filter factor cho phần còn lại khi tổng boost lớn hơn `+12 dB`.
