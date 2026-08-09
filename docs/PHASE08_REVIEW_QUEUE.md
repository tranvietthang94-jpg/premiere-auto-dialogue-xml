# Phase 08 — Shadow evidence và danh sách review

Trạng thái: `in-progress`.

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

## Hợp đồng an toàn đã khóa

- Shadow evidence không được đổi `Status`, `Enabled`, phrase gain, marker hoặc XML audio output.
- Mọi vùng mơ hồ tiếp tục Enabled; M19/A3 tại `00:07:28:15` phải được giữ.
- Không thay preset Cân bằng, Silero VAD `6.2.1`, target hậu routing `-6 dBFS`, bù center-pan hoặc trần boost `+18 dB`.
- Không sửa/ghi đè XML, WAV hoặc `.prproj` nguồn; mỗi run dùng thư mục output mới.
- Không commit media thật, audit đầy đủ, output, installer, chứng thư hoặc private artifact.

## Cổng nghiệm thu Phase 08A

- Toàn bộ test Release hiện có và test mới đạt.
- HGE2 giữ nguyên về mặt semantic toàn bộ fragment status, Enabled/Disabled, phrase gain và marker so với baseline Phase 07.
- M19 vẫn Enabled và xuất hiện trong review mức ưu tiên cao.
- Mọi marker mơ hồ được ánh xạ vào đúng một review group; không mất coverage khi gộp.
- CSV UTF-8 mở được với tiếng Việt, không chứa đường dẫn media tuyệt đối và luôn kèm frame/timecode để editor tra trong Premiere.
- Nếu XML audio thay đổi ngoài dự kiến, dừng Phase 08A và mở cổng Premiere round-trip mới; không âm thầm chấp nhận khác biệt.

## Ngoài phạm vi

- Không tự động chuyển vùng shadow-likely-bleed thành Disabled.
- Không cho phép người dùng áp override hoặc sinh XML đã duyệt trong app.
- Chưa thêm playback/review UI; phần này chỉ bắt đầu ở Phase 08B sau khi dữ liệu Phase 08A chứng minh hữu ích.
- Chưa mở rộng frame rate, media format, overlap, effect, automation, submix, nested sequence hoặc multicam.
