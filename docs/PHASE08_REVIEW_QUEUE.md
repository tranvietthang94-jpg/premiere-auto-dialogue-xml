# Phase 08 — Shadow evidence và danh sách review

Trạng thái: `planned`.

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
