# Phase 06 — đánh giá nhãn Context và lớp bảo vệ VAD/năng lượng

Trạng thái: `incomplete`; dữ liệu nghe ngày 2026-08-04 cung cấp bằng chứng vận hành hữu ích nhưng không được dùng trực tiếp để đóng cổng nhãn Mục tiêu.

## Phạm vi dữ liệu người vận hành

- Người nghe: Thắng Trần.
- Đã điền 63/63 dòng mà không mở khóa dự đoán trước khi nghe.
- Người nghe chỉ đánh giá toàn khoảng **Context** bên trái, không cô lập khoảng **Mục tiêu** bên phải. Context chứa Mục tiêu và thêm 12 frame ở mỗi phía.
- Nhãn Context thô gồm 18 `L`, 29 `N` và 16 `M`.
- Không tính ba tỷ lệ nghiệm thu từ các nhãn thô này. Nếu tính máy móc sẽ trộn tiếng ở hai phía vào Mục tiêu và tạo cả false positive lẫn false negative giả.

File nhãn và khóa dự đoán đầy đủ được giữ ngoài Git trong `private-artifacts/phase06-label-gate/`.

## Cách các ghi chú được sử dụng

Các timecode do người vận hành ghi giúp phân biệt ba loại tình huống:

### Context chạm lời ở ngoài Mục tiêu

- `M01`, `M07`, `M28`, `M40`, `M54` và `M58`: lời bắt đầu sau cuối Mục tiêu từ 1–6 frame nhưng nằm trong phần Context cộng thêm; không phải bằng chứng app cắt lời trong Mục tiêu.
- `M24`, `M59`: lời bắt đầu sau Mục tiêu và fragment hiện tại là `ambiguous` Enabled; hành vi bảo thủ đúng.
- `M22` và `M35`: fragment noise kết thúc đúng tại frame mà fragment speech kế tiếp bắt đầu. Nhãn Context nghe thấy speech kế bên không đủ để gọi đây là false negative.

### Mục tiêu thực sự chứa hoặc chạm lời

- `M05`, `M41`, `M43`, `M49`, `M60`: lời nằm trong fragment speech Enabled. Ghi chú còn chỉ ra phần noise được giữ trong padding; đây là chi phí bảo thủ chấp nhận được để không lẹm câu.
- `M25`: speech rất ngắn chạm khoảng Mục tiêu; fragment là `ambiguous` Enabled.
- `M44`: ranh giới lời do người nghe ghi chạm khoảng một frame đầu của fragment noise. Đây là ca biên cần kiểm tra lại trên Mục tiêu nếu dùng làm nhãn nghiêm ngặt.
- `M39`: toàn bộ Mục tiêu nằm trong khoảng noise mà người nghe ghi; fragment noise Disable phù hợp.

### Tín hiệu cần sửa thực sự: M19

`M19`, track A3, có lời được người nghe xác định tại `00:07:28:15`, nằm bên trong fragment noise Disable `00:07:25:05–00:07:35:07`.

Chẩn đoán read-only từ đúng XML/media cho thấy:

- Silero VAD cao nhất trong toàn fragment chỉ `0,07245165`, có 0 block đạt VAD `0,50` và 0 block direct evidence theo luật cũ.
- Đúng quanh `00:07:28:14–00:07:28:17` có bốn block liên tiếp, tổng `128 ms`, RMS lớn hơn noise floor thích nghi khoảng 11–14 dB.
- Tại `00:07:28:15`, A3 có RMS `-42,91 dBFS`; sáu mic khác nằm từ `-52,36` đến `-59,92 dBFS`. A3 lớn hơn mic gần nhất khoảng `9,45 dB`, nên không có bằng chứng mic khác lớn hơn A3 ít nhất 12 dB để coi đây là bleed.
- Report VAD riêng có SHA-256 `AA51B87E8F52C6A45F826DAA9168B4549C758A8E6704AF1D6D32E458622BEADC` và được giữ ngoài Git.

Kết luận: VAD và năng lượng trực tiếp xung đột. Theo nguyên tắc MVP, app phải giữ và đánh dấu thay vì Disable.

## Lớp bảo vệ mới

Preset `Cân bằng` giữ một vùng là `ambiguous` khi đồng thời:

- VAD không đạt `0,50`;
- RMS vẫn cao hơn noise floor thích nghi ít nhất `10 dB`;
- điều kiện này kéo dài liên tục ít nhất `120 ms`.

Reason audit là `ambiguous-energy-vad-conflict` hoặc `ambiguous-energy-vad-conflict-near-speech`. Mọi fragment thuộc hai reason này phải Enabled. Audit nâng lên schema `1.3` và ghi `preserveVadNegativeHighEnergyConflicts=true`.

Mô phỏng trước khi tích hợp trên HGE2 tìm thấy 1.843 chuỗi năng lượng/VAD xung đột; nếu giữ toàn bộ phần chồng noise, app vẫn Disable khoảng `95,84%` thời lượng mà candidate cũ phân loại là noise. Đây chỉ là so sánh máy-với-máy, không phải tỷ lệ `clear-noise` do người nghe xác nhận.

## Candidate audit 1.3

Candidate mới được tạo từ input SHA-256 `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`; SHA input trước/sau giống nhau.

- SHA-256 XML output: `5ADF7F4717E8A06330B6F0A73F6C1AED4FCC444FA63101A7A3BD995A015CEDCD`, khớp audit.
- SHA-256 audit: `95C8C430861757FA1341A57180D6B3188F4F793C28232CA30F47AB732B20B182`.
- Runtime khoảng `30,0 s`, peak working set `169,4 MB`; 7 warning bit-depth dự kiến, 0 error.
- 10.468 fragment, 4.775 marker, 2.132 phrase; 935 phrase không cap và 1.197 phrase cap.
- 1.885 fragment energy/VAD conflict đều Enabled; `M19` trở thành `ambiguous` Enabled tại frame `11214–11218`.
- Noise còn 9.509,248 giây trên tổng bảy track, bằng khoảng `96,80%` thời lượng noise của candidate cũ; phần còn lại chuyển sang ambiguous Enabled.
- 2.129/2.132 quyết định gain không đổi. Ba phrase A3/A4/A5 đổi `0,143–0,291 dB` do frame Enabled mới tham gia gain reference; toàn bộ 935 phrase không cap vẫn có predicted post-routing peak bằng `-6 dBFS` trong sai số số thực dưới `0,000001 dB`.

Candidate audit `1.2` cũ vẫn là bằng chứng PCM lịch sử hợp lệ cho chính XML cũ, nhưng không được dùng để tuyên bố XML `1.3` đã qua round-trip. Candidate mới cần import Premiere và PCM mới trước khi đóng cổng.

## Kiểm thử mã

- Thêm API evidence dùng chung giữa analyzer và công cụ chẩn đoán để report không sao chép khác logic production.
- Thêm chế độ read-only `--vad-window`; report mới được ghi atomic, từ chối ghi đè và không chứa đường dẫn media nguồn.
- Test xác nhận chuỗi xung đột 128 ms được giữ ambiguous, còn transient 96 ms vẫn là noise.
- Release suite đạt 93/93.

## Gói candidate audit 1.3

- ZIP self-contained `win-x64`, chưa phải installer.
- 410 payload file; không có Python hoặc PDB.
- Kích thước `71.105.313` byte; SHA-256 `AAB87C47250792CC16C3FC23CB3226453D15AE012FA0F0AF003D81F1CBAFE1AF`.
- Manifest ghi model `6.2.1` và SHA-256 `1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3`.
- Gói chỉ dùng cho pilot Premiere và máy Windows sạch; chưa được phát hành và chưa có bằng chứng chạy trên OS sạch.
