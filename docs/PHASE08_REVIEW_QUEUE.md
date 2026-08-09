# Phase 08 — Shadow evidence và danh sách review

Trạng thái: `passed` ngày 2026-08-09.

## Mục tiêu

- Giảm công rà soát hàng nghìn marker bằng danh sách review được gom nhóm và xếp ưu tiên theo track/timecode.
- Đối chiếu thêm waveform giữa các mic cho vùng VAD thấp nhưng năng lượng cao ở chế độ shadow.
- Giữ nguyên luồng tự động hiện tại: app vẫn phân tích và xuất XML mới trong một lượt.
- Thu thập bằng chứng đủ rõ trước khi cân nhắc tự động Disable thêm bleed ở phase sau.

## Phạm vi Phase 08A

- Tạo bằng chứng đa mic cho các vùng `ambiguous-energy-vad-conflict*` mà không thay đổi trạng thái phân tích.
- Ghi kết quả tư vấn vào audit mới và xuất CSV review tiếng Việt theo track, timecode, mức ưu tiên và mic đối chiếu.
- Gộp các fragment mơ hồ gần nhau trên cùng track để một sự kiện không tạo quá nhiều dòng review.
- Chạy lại fixture HGE2 và full HGE để đo số mục review, runtime và peak RAM.

## Implementation slice 1 — 2026-08-09

- `AudioProjectAnalyzer` tạo shadow evidence sau bước resolve bleed và giữ riêng trong `ProjectAudioAnalysis`.
- Bộ tạo evidence chỉ nhận các segment `ambiguous-energy-vad-conflict*`, dùng interval index theo phrase để tránh quét toàn bộ timeline cho mỗi vùng.
- Kết quả phân biệt `NoComparableSpeech`, `BelowThreshold`, `ConflictingEvidence` và `LikelyBleed`; các số đo track đối chiếu, advantage, correlation, lag và residual được giữ để dùng ở slice audit/CSV.
- Bằng chứng shadow không sửa `TrackAudioAnalysis`; XML writer hiện không đọc dữ liệu mới này.
- Bộ test Release đạt `100/100`, gồm ca likely bleed, residual xung đột, dưới threshold, không có lời chồng, bỏ qua ambiguity ngoài phạm vi, cancellation và tích hợp analyzer không đổi status.

## Implementation slice 2 — 2026-08-09

- Mỗi run mới xuất thêm `<sequence>_AutoAudio.review.csv`; app hiển thị tên CSV và số review group trong tóm tắt kết quả.
- Audit nâng từ schema `1.3` lên `1.4`, lưu SHA-256 của CSV, quy ước timecode `sequence-relative-ndf`, toàn bộ shadow evidence và danh sách group đã tạo.
- Marker `Cần kiểm tra` trên cùng track được gộp khi khoảng cách không quá `12` frame. Kiểm tra coverage bắt buộc tổng số marker trong group phải bằng toàn bộ marker mơ hồ; marker cảnh báo `gain-capped` vẫn giữ trong XML/audit cũ nhưng không đi vào review group mơ hồ.
- Mức ưu tiên là bằng chứng tư vấn: xung đột trực tiếp/bleed hoặc energy/VAD chưa được mọi đối chiếu xác nhận là likely bleed ở mức cao; energy/VAD có toàn bộ đối chiếu likely bleed và `ambiguous-independent` ở mức vừa; chỉ `ambiguous-near-speech` ở mức thấp.
- CSV dùng UTF-8 BOM, CRLF, cột tiếng Việt, frame và timecode tương đối; chỉ ghi basename của media và trung hòa tên tệp có tiền tố công thức bảng tính.
- XML, CSV và audit đều được ghi qua tệp tạm, đọc/kiểm tra lại rồi mới công bố vào thư mục run; lỗi giữa chừng xóa cả artifact tạm lẫn artifact đã di chuyển của run đó.
- Bộ test Release đạt `104/104`. Ca hồi quy cố định UUID chứng minh thêm/bớt riêng shadow evidence làm audit/CSV thay đổi nhưng SHA-256 XML không đổi.

## Implementation slice 3 — output streaming và đo tải thật

- Lượt full HGE đầu tiên phát hiện peak toàn tiến trình `5.247,0 MB` trong lúc giữ đồng thời cây XML sinh mới và cây XML đọc lại; số `728,6 MB` chụp ngay sau analysis không được dùng thay peak toàn run.
- Bỏ cây đọc lại giảm peak xuống `1.848,6 MB` nhưng vẫn vượt cổng `1,5 GB`. Cổng không được hạ; package writer được đổi sang lập generation plan rồi phát từng fragment trực tiếp qua `XmlWriter`.
- Streaming writer chỉ giữ XML nguồn nhỏ và một fragment đang ghi; vẫn kiểm source SHA-256, số track/clip, ID duy nhất, coverage generation plan, số fragment/marker đã phát, SHA-256 file kết quả, DOCTYPE nguyên văn, well-formedness và root `xmeml`.
- Ca hồi quy cố định UUID xác nhận XML streaming bằng XML DOM cũ trên fixture tổng hợp. Bộ test Release tăng lên `105/105`.
- Tool pilot báo cả peak sau analysis lẫn peak toàn run và tên/số lượng review output, tránh lặp lại phép đo thiếu pha writer.

## Kết quả pilot Phase 08A — 2026-08-09

### HGE2

- Input `test HGE2.xml` giữ SHA-256 `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`; inspector có 7 warning metadata dự kiến và 0 error.
- Run streaming hoàn tất trong `42,7 giây`, peak toàn tiến trình `174,2 MB`; có 2.132 phrase, 10.468 fragment và 4.775 marker.
- So với candidate audit `1.3` Phase 07: 0 khác biệt semantic fragment và 0 khác biệt marker trên toàn bộ tập.
- 3.578 marker mơ hồ được phủ đúng 3.578/3.578 vào 2.251 review group: 1.145 cao, 579 vừa, 527 thấp.
- Có 2.096 shadow evidence: 1.387 `BelowThreshold`, 709 `NoComparableSpeech`; không có `LikelyBleed` hoặc `ConflictingEvidence` trên fixture này.
- M19/A3 frame `11214–11218` vẫn `ambiguous`, Enabled và nằm trong group `R-T03-000048` ưu tiên cao; shadow outcome là `NoComparableSpeech`.
- CSV đúng SHA-256 trong audit, có UTF-8 BOM và không chứa đường dẫn media tuyệt đối.

### Full HGE

- Input `test HGE.xml` giữ SHA-256 `4497FBBA2E6D5B834AA73929D6A3C6BADF9392BC1F0168E583411A9515ABCE90`; inspector có 27 warning compatibility dự kiến và 0 error.
- Run streaming hoàn tất trong `25 phút 6 giây`, peak toàn tiến trình `785,6 MB`; đạt mục tiêu dưới 90 phút và dưới 1,5 GB.
- Kết quả có 43.382 phrase, 253.590 fragment, 124.071 marker và 52.069 review group.
- 97.453 marker mơ hồ được phủ đúng 97.453/97.453: 27.088 group cao, 10.986 vừa và 13.995 thấp.
- Có 52.943 shadow evidence: 34.153 `BelowThreshold`, 18.790 `NoComparableSpeech`; không có `LikelyBleed` hoặc `ConflictingEvidence` trên fixture này.
- So với lượt DOM cùng code ngay trước tối ưu, audit có 0 khác biệt semantic trên 9.062.247 dòng sau khi bỏ sáu trường run/ID ngẫu nhiên. CSV giống hệt với SHA-256 `8BE139B982B547EDBC8C9DA43DF252E157997CF2AD48781436A6DA7F7E8ED834`.
- Hai XML đều dài 361.520.333 byte; phép so sánh 11.110.687 dòng sau khi chuẩn hóa sequence UUID và generated ID cho kết quả 0 khác biệt.
- Run cuối không còn `.tmp` và chỉ công bố đúng XML, audit và CSV trong thư mục run mới.

Toàn bộ media, audit đầy đủ, CSV và XML pilot được giữ ngoài Git dưới `private-artifacts/phase08-*`; không file dữ liệu thật nào được commit.

## Hợp đồng an toàn đã khóa

- Shadow evidence không được đổi `Status`, `Enabled`, phrase gain, marker hoặc XML audio output.
- Mọi vùng mơ hồ tiếp tục Enabled; M19/A3 tại `00:07:28:15` phải được giữ.
- Không thay preset Cân bằng, Silero VAD `6.2.1`, target hậu routing `-6 dBFS`, bù center-pan hoặc trần boost `+18 dB`.
- Không sửa/ghi đè XML, WAV hoặc `.prproj` nguồn; mỗi run dùng thư mục output mới.
- Không commit media thật, audit đầy đủ, output, installer, chứng thư hoặc private artifact.

## Cổng nghiệm thu Phase 08A

- Đạt: toàn bộ `105/105` test Release hiện có và test mới đạt.
- Đạt: HGE2 giữ nguyên semantic toàn bộ fragment status, Enabled/Disabled, phrase gain và marker so với baseline Phase 07.
- Đạt: M19 vẫn Enabled và xuất hiện trong review mức ưu tiên cao.
- Đạt: mọi marker mơ hồ trên HGE2 và full HGE được ánh xạ vào đúng một review group; không mất coverage khi gộp.
- Đạt: CSV UTF-8 tiếng Việt không chứa đường dẫn media tuyệt đối và luôn kèm frame/timecode để editor tra trong Premiere.
- Đạt: full HGE hoàn tất dưới 90 phút và peak RAM dưới 1,5 GB.
- Nếu XML audio thay đổi ngoài dự kiến, dừng Phase 08A và mở cổng Premiere round-trip mới; không âm thầm chấp nhận khác biệt.

## Ngoài phạm vi

- Không tự động chuyển vùng shadow-likely-bleed thành Disabled.
- Không cho phép người dùng áp override hoặc sinh XML đã duyệt trong app.
- Chưa thêm playback/review UI; phần này chỉ bắt đầu ở Phase 08B sau khi dữ liệu Phase 08A chứng minh hữu ích.
- Chưa mở rộng frame rate, media format, overlap, effect, automation, submix, nested sequence hoặc multicam.
