# Phase 06 — đánh giá nhãn Context và lớp bảo vệ VAD/năng lượng

Trạng thái: `closed-with-waiver`; dữ liệu nghe ngày 2026-08-04 cung cấp bằng chứng vận hành hữu ích nhưng không được dùng trực tiếp để tính các tỷ lệ nhãn Mục tiêu. Chủ dự án đã miễn cổng nghe Mục tiêu ngày 2026-08-04.

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

## Premiere round-trip candidate audit 1.3

Người vận hành import candidate mới, xác nhận A3 tại `00:07:28:15` vẫn nghe được, rồi export A1–A7 riêng từ đầu đến hết sequence. Cả bảy WAV đều là mono integer PCM 48 kHz/24-bit và có đúng 103.219.200 sample.

| Track | Phrase đạt | Không cap đạt target | SHA-256 WAV | SHA-256 report |
|---|---:|---:|---|---|
| A1 | 252/252 | 71/71 | `4B6244EB0F49620A5837C4539E2F25F8B613473E2B64431737D41379AEE94E9E` | `ACAC85CF184D0C847067A21E5FE8556C161149579D69464CA9D57E760ECCF62E` |
| A2 | 286/286 | 69/69 | `F47B2F8EB5331310BE3622B51D7AACC045A6A83087C6267390062656F1CBF124` | `D181BB9F303268FAC88594E51970A13AA9E36225A3847778379E39DD574ED330` |
| A3 | 389/389 | 143/143 | `F0F8CAAA784323F8F8053554C1950EC14F61ACE052112DF1D328F8324D92B98F` | `BC89CD92E58CCD0699BFA6B72867F412666B3664F638FEC32FD9ABC1CDA3843E` |
| A4 | 213/213 | 95/95 | `FE36DDE21035402D85710845CACFA3DF7E83ED8FBE8F4129355BDA9366EEF916` | `A6350122013D55FF02A0228914E2A2DD543AE9B8A71BC2AC68400BB515AF6DEC` |
| A5 | 360/360 | 248/248 | `B4EA49E303FC6C280F33CF38ED4A7B6CEDC3227D6012A292A2C1D1397B8DAA82` | `E668A3E69A71D49A09D37E50A000ECF1D3FC668653C5D6EC0754EAEF38343FC2` |
| A6 | 279/279 | 127/127 | `9AAB21E6292F5667FBB2B57DD4D45A61B18000C0DE44D82D31986ECFA5685E95` | `D221FB2F42E3CE4429597F0E311707CE09F5CAEA652EFBE1DCC7B6B6A3341DE9` |
| A7 | 353/353 | 182/182 | `39263BF9CF19AF49F2CB2E9DF56FBBA85179075D6AD17FB06AFF67E2C9AE78F2` | `4B2A0482EF754368B117DE415004C8D2CDA95B34BB92B46D407CDE15F4343EA9` |

Tất cả report liên kết cùng audit SHA-256 `95C8C430861757FA1341A57180D6B3188F4F793C28232CA30F47AB732B20B182` và source XML SHA-256 `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`. Hash WAV đọc lại khớp giá trị đã khóa trong từng report.

Tổng hợp: 2.132/2.132 phrase đạt gain/timing; 935/935 phrase không cap đạt target; 1.197 phrase cap khớp dự đoán và không phrase nào nóng hơn `-6 dBFS`. Peak nhóm không cap nằm từ `-6,000019` đến `-5,999974 dBFS`; phrase cap nóng nhất `-6,004388 dBFS`; sai lệch source-linked tuyệt đối lớn nhất dưới `0,000027 dB`. Cổng PCM candidate audit `1.3` đạt.

Kết quả này xác nhận gain/timing và M19 qua Premiere, nhưng không biến nhãn Context thành nhãn Mục tiêu và không thay bằng chứng Windows sạch.

## Translation Results và khả năng phục hồi

Ngày 2026-08-04, người vận hành xác nhận:

- Premiere không hiện cửa sổ **FCP Translation Results** khi import candidate audit `1.3`; không có cảnh báo dịch XML hiển thị để lưu. Bằng chứng này được ghi đúng là “không có cửa sổ/cảnh báo hiển thị”, không được mô tả thành một report mà Premiere không tạo.
- Một clip đang Disable được bật lại thủ công và audio gốc nghe bình thường. Cổng phục hồi non-destructive đạt; app không xóa audio nguồn khỏi timeline.

Hai xác nhận trên cùng việc M19 nghe được và PCM A1–A7 đạt đóng phần import/khả năng phục hồi của candidate `1.3`. Cổng nhãn Mục tiêu đã được chủ dự án miễn; các tỷ lệ 100%/90%/0% tương ứng không được tuyên bố. Cổng máy Windows sạch vẫn chưa đạt.
