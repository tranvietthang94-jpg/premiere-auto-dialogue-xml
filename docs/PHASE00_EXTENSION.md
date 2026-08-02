# Phase 00 mở rộng — tìm encoding gain cao

## Cơ sở

- Apple định nghĩa volume của audio clip trong XMEML bằng effect `Audio Levels`; tham số `Level` là hệ số tuyến tính `10^(dB/20)`: [XMEML Topics](https://developer.apple.com/library/archive/documentation/AppleApplications/Reference/FinalCutPro_XML/Topics/Topics.html).
- Adobe ánh xạ Audio Levels của Final Cut Pro XML sang audio clip volume và cảnh báo audio gain/level có thể không truyền chính xác: [Importing XML files](https://helpx.adobe.com/dk/premiere-pro/using/importing-xml-project-files-final.html), [Export Final Cut Pro XML](https://helpx.adobe.com/premiere/desktop/render-and-export/export-files/export-a-project-as-a-final-cut-pro-xml-file.html).
- Round-trip đầu tiên đã chứng minh Premiere Pro 2026 chỉ giữ một effect khi XML chứa hai `Audio Levels` giống nhau.

Tài liệu XMEML không cung cấp một phần tử clip-gain độc lập thứ hai có cam kết tương thích với Premiere. Vì vậy thử nghiệm ưu tiên các biến thể của cơ chế chuẩn trước khi xem xét thay đổi hợp đồng output.

## Ma trận thử nghiệm

Mỗi ca dài hai giây và dùng một source trim riêng trên cùng WAV tổng hợp:

| Ca | Encoding đầu vào | Mục đích |
|---|---|---|
| Unity | không Audio Levels | Mốc đo PCM |
| `+12 dB` | một level, trần `+12 dB` | Mốc tương thích đã biết |
| `+15 dB` | một level, trần khai báo `+15 dB` | Tìm giới hạn importer |
| `+18 dB`, trần mở rộng | một level `7.943282`, trần khai báo `7.943282` | Thử single-level vượt trần cũ |
| `+18 dB`, trần cũ | một level `7.943282`, trần khai báo `3.98109` | Xác định `valuemax` có chi phối clamp hay không |
| `+12 +6 dB` | hai Audio Levels | Xác định effect đầu hay cuối được giữ |
| `+6 +12 dB` | hai Audio Levels, đảo thứ tự | Xác định quy tắc chuẩn hóa |

Một encoding chỉ được chấp nhận khi đồng thời:

- XML Premiere re-export vẫn thể hiện tổng gain `+18.0 ±0.1 dB`;
- PCM render tăng `+18.0 ±0.1 dB` so với unity;
- timing, source trim và `pproTicksIn/Out` không đổi.

Nếu không biến thể nào đạt, Phase 00 vẫn bị chặn. Không suy diễn từ UI và không chọn phương pháp chỉ đúng trong XML nhưng sai trong PCM.

## Kết quả vòng 1 trên Premiere Pro 2026

| Ca | XML re-export | PCM tương đối | Kết quả |
|---|---:|---:|---|
| Unity | `0 dB` | `0 dB` | Mốc đạt |
| `+12 dB` | `+12 dB` | `+12 dB` | Đạt |
| `+15 dB`, trần mở rộng | `+12 dB` | `+15 dB` | PCM nhận `+15`, nhưng XML không round-trip trung thực |
| `+18 dB`, trần mở rộng | `+12 dB` | `+15 dB` | Bị clamp ở `+15 dB` |
| `+18 dB`, trần cũ | `+12 dB` | `+15 dB` | Bị clamp ở `+15 dB` |
| `+12 +6 dB` | `+6 dB` | `+6 dB` | Chỉ effect cuối được giữ |
| `+6 +12 dB` | `+12 dB` | `+12 dB` | Chỉ effect cuối được giữ |

Không có encoding `+18 dB` nào đạt đồng thời XML và PCM. Kết quả cũng cho thấy Premiere có giới hạn clip volume nội bộ `+15 dB`, trong khi FCP XML re-export chuẩn hóa giá trị lớn hơn `+12 dB` về `+12 dB`.

SHA-256 của bằng chứng cục bộ không commit:

- Premiere XML: `8E47829C6551CF468660B138F948F3D8F2F90C58974061A02CCA22C11F3B337A`
- Premiere WAV: `039897D1C0F4E6DC8BC89E82B2AE5AC6EAB38BE524FD4D2F0771E16D621652DC`

Thử nghiệm kế tiếp là tạo một clip có `Audio Gain +3 dB` bằng Premiere, export FCP XML và kiểm tra xem gain này có được mã hóa thành dữ liệu độc lập với `Audio Levels` hay không. Chỉ khi trường đó tồn tại và sống qua một lần import thứ hai mới có thể xem xét tổ hợp `+15 dB` clip volume với `+3 dB` audio gain.
