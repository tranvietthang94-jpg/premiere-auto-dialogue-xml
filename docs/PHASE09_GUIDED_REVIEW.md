# Phase 09 — Không gian review có hướng dẫn

Trạng thái: `kickoff` ngày 2026-08-09.

## Quyết định mục tiêu

Biến danh sách review của Phase 08 thành một workflow human-in-the-loop ngay trong app: editor có thể mở một run đã xuất, ưu tiên đúng mục cần xem, nghe ngữ cảnh cục bộ, ghi quyết định và tiếp tục phiên review về sau mà không phải sửa XML, WAV hoặc audit gốc.

Phase 09 chỉ tạo bằng chứng và quyết định review tách rời. Không quyết định nào được tự động đổi `Status`, `Enabled`, gain, marker hoặc XML audio. Việc áp quyết định để sinh một XML khác chỉ được cân nhắc ở phase sau, sau khi dữ liệu review thật chứng minh đủ an toàn.

## Vì sao đây là bước tiếp theo

- Phase 08 đã tạo được review group có coverage đầy đủ, nhưng full HGE vẫn có `52.069` group từ `97.453` marker mơ hồ. CSV giúp sắp xếp nhưng chưa tạo thành quy trình review có thể tạm dừng và tiếp tục.
- Shadow evidence trên HGE2 và full HGE chưa tạo trường hợp `LikelyBleed` hoặc `ConflictingEvidence`. Chưa có cơ sở để tăng mức tự động Disable.
- App hiện chỉ báo tên CSV và mở Explorer. Editor vẫn phải tự tìm timecode, tự nghe trong Premiere và tự quản lý phần đã xem.
- Giảm ma sát review trước sẽ tạo được nhãn quyết định đáng tin cậy cho bước nâng cấp tự động hóa sau này mà không đánh đổi nguyên tắc giữ vùng mơ hồ.

## Kết quả sản phẩm mong muốn

Sau Phase 09, editor có thể:

1. Mở không gian review ngay sau một lượt xuất hoặc chọn lại một run hợp lệ đã có.
2. Xem tổng số và tiến độ theo ưu tiên, track, lý do và kết quả shadow; tìm theo mã group hoặc timecode.
3. Duyệt danh sách lớn bằng UI ảo hóa, không nạp toàn bộ audit hàng triệu dòng vào RAM.
4. Nghe target mic và mic đối chiếu quanh đúng khoảng timeline khi XML nguồn và media đã được kiểm tra lại thành công.
5. Ghi một trong các quyết định `Giữ`, `Đề nghị tắt`, `Chưa rõ`, kèm ghi chú tùy chọn.
6. Đóng app rồi tiếp tục từ checkpoint gần nhất mà vẫn truy ra đúng audit/review package đã dùng.
7. Xuất tóm tắt tiến độ và quyết định để kiểm tra độc lập; XML kết quả Phase 08 vẫn bất biến.

## Phạm vi triển khai

### Slice 09A — hợp đồng review package

- Tạo manifest review gọn cho mỗi run mới, chứa group, bằng chứng cần hiển thị và dữ liệu ánh xạ tối thiểu; không sao chép toàn bộ fragment audit.
- Gắn manifest bằng SHA-256 với source XML, output XML, audit và CSV. Không ghi đường dẫn media tuyệt đối vào artifact.
- Reader kiểm tra schema, hash, số group, coverage và giới hạn kích thước trước khi mở.
- Có đường tương thích cho run Phase 08 chỉ có audit `1.4` và CSV: cho phép xem danh sách cơ bản; tính năng nghe chỉ mở khi đủ dữ liệu và XML/media nguồn khớp.
- Mọi writer dùng tệp tạm, xác minh rồi mới công bố; lỗi hoặc cancel không để artifact dở dang.

### Slice 09B — UI hàng đợi và checkpoint quyết định

- Thêm lối vào `Review kết quả` nhưng giữ luồng chính bốn bước hiện tại đơn giản và dễ thấy.
- Danh sách ảo hóa, mặc định ưu tiên `Cao` trước; có filter ưu tiên/track/lý do/shadow và tìm mã/timecode.
- Panel chi tiết hiển thị timecode, frame, nguồn media dạng basename, marker count và bằng chứng mic đối chiếu bằng tiếng Việt dễ hiểu.
- Quyết định review được lưu thành sidecar checkpoint mới, có schema/version, SHA-256 review package/audit, thời điểm và lịch sử quyết định. Không sửa XML, audit, CSV hoặc checkpoint cũ.
- Khi hash hoặc group identity không khớp, app từ chối tiếp tục phiên cũ và giải thích rõ; không tự remap quyết định.

### Slice 09C — nghe ngữ cảnh offline

- Chỉ bật nghe sau khi parse lại đúng source XML, xác nhận SHA-256 và kiểm tra media như luồng chính.
- Ánh xạ timeline sang source range bằng metadata đã kiểm chứng; hỗ trợ source trim, gap, nhiều WAV nối tiếp và group qua ranh giới file trong phạm vi MVP hiện tại.
- Cho nghe target mic trước; mic đối chiếu chỉ hiện khi evidence hợp lệ. Có khoảng ngữ cảnh ngắn trước/sau nhưng luôn chỉ rõ ranh giới group.
- Playback đọc PCM cục bộ, không normalize, không ghi đè WAV và không dùng cloud/API/telemetry. Tệp tạm nếu cần phải nằm ngoài media nguồn và được dọn an toàn.
- Không gọi playback là bằng chứng peak, LUFS, true peak hoặc Premiere Master bus.

### Slice 09D — pilot và khóa cổng

- HGE2: M19/A3 tại `00:07:28:15` vẫn xuất hiện ưu tiên cao, nghe được từ đúng source range và quyết định checkpoint có thể lưu/mở lại.
- Full HGE: mở đủ `52.069` group, tổng ưu tiên/track/reason khớp audit Phase 08 và thao tác filter không cần deserialize toàn bộ audit.
- Đo thời gian mở, độ trễ filter, peak RAM và kích thước review package trên đúng máy pilot; mục tiêu ban đầu là màn hình đầu tiên dưới `10 giây` và peak toàn tiến trình review dưới `1,0 GB`.
- Hash XML, WAV, audit và CSV trước/sau pilot không đổi. Không commit media, audit đầy đủ, quyết định người dùng hoặc output thật.

## Cổng nghiệm thu

- Toàn bộ `105/105` test Release hiện có tiếp tục đạt; có test mới cho manifest, hash mismatch, giới hạn input, list/filter, checkpoint, resume, mapping source và cancel.
- Một review group không thể mất hoặc xuất hiện hai lần giữa manifest, UI và tóm tắt quyết định.
- UI vẫn phản hồi khi mở full HGE; không tạo một cây DOM cho audit đầy đủ.
- Quyết định `Đề nghị tắt` không làm thay đổi XML hoặc trạng thái phân tích ở Phase 09.
- M19 vẫn Enabled trong XML và luôn có thể truy ra group review tương ứng.
- Run cũ thiếu media vẫn xem được metadata cơ bản, nhưng nút nghe bị khóa fail-safe; app không suy đoán đường dẫn hoặc routing.
- Đóng app, cancel hoặc lỗi I/O không làm hỏng artifact gốc và không để checkpoint được coi là hợp lệ khi chưa ghi xong.
- UI chính và thông báo lỗi bằng tiếng Việt; chi tiết kỹ thuật nằm trong khu vực thu gọn hoặc panel review chi tiết.

## Ngoài phạm vi

- Không tự động Disable thêm bleed/noise từ shadow evidence.
- Không áp quyết định review để sinh XML đã duyệt; đây là một cổng riêng cho phase sau.
- Không điều khiển Premiere, tự nhảy playhead hoặc phụ thuộc plugin Premiere.
- Không mở rộng frame rate, stereo/multichannel source, codec, overlap, effect, automation, submix, nested sequence, multicam hoặc retime.
- Không đồng bộ cloud, tài khoản, telemetry hoặc cộng tác nhiều người.
- Không thay preset Cân bằng, Silero VAD `6.2.1`, target hậu routing `-6 dBFS`, compensation center-pan hoặc trần boost `+18 dB`.

## Bước triển khai đầu tiên

Bắt đầu bằng Slice 09A: chốt schema review package và checkpoint, viết reader/writer cùng test hash/coverage/giới hạn trước khi thêm UI. Cách này khóa provenance và khả năng tải full HGE trước, tránh xây UI trên một định dạng chưa an toàn hoặc buộc app đọc toàn bộ audit lớn.
