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
