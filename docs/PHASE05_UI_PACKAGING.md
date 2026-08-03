# Phase 05 — giao diện Windows và đóng gói offline

## Mục tiêu

Phase 05 nối các thành phần đã khóa ở Phase 02–04 thành một ứng dụng WPF tiếng Việt có bốn bước chính:

1. Chọn XML và thư mục kết quả.
2. Kiểm tra XML/media bằng thao tác chỉ đọc.
3. Phân tích âm thanh cục bộ bằng preset **Cân bằng**.
4. Ghi XML/audit vào thư mục run mới và mở vị trí kết quả.

Luồng chính chỉ hiển thị thông tin editor cần để quyết định tiếp tục. Mã lỗi, threshold, model/checksum và compatibility warning nằm trong phần **Chi tiết kỹ thuật** thu gọn.

## Trạng thái và an toàn

- Chỉ cho phân tích khi lần kiểm tra gần nhất đạt và XML/thư mục output chưa thay đổi.
- Trong khi chạy, khóa nút chọn tệp và nút chạy; hiển thị tiến độ theo track và chỉ cho phép **Dừng an toàn**.
- Dừng hoặc đóng cửa sổ phải phát cancellation vào analyzer/writer, chờ worker kết thúc rồi mới cho app thoát.
- Hủy trước khi writer hoàn tất không được để lại XML/audit cuối hoặc file tạm.
- Mỗi lỗi hiển thị thông báo tiếng Việt ngắn và ghi diagnostic JSON có giới hạn vào thư mục kết quả; diagnostic không chứa audio sample, model data hoặc telemetry.
- Không sửa, di chuyển, đổi tên hoặc ghi đè XML/WAV/project Premiere nguồn.
- Sau khi thành công, UI hiển thị tên sequence, số phrase/fragment/marker, đường dẫn output và nút mở Explorer.

## Đóng gói

- Publish self-contained `win-x64`, framework-dependent/Python/cloud/API/telemetry không nằm trong runtime contract.
- Giữ model ONNX đã ghim và ONNX Runtime trong thư mục publish; không tải thêm sau cài đặt.
- Không trim assembly vì ONNX/WPF cần tương thích ổn định; không gộp single-file trước pilot.
- Script đóng gói phải tạo thư mục mới, không ghi đè artifact cũ, rồi kiểm tra executable, DLL/runtime native và model checksum.
- Phase 05 chỉ tạo publish folder/ZIP để kiểm thử. Installer chỉ được tạo sau cổng Premiere pilot ở Phase 06.

## Cổng nghiệm thu

- Unit test state machine: chọn lại input reset inspection; lỗi inspection chặn run; success/cancel/failure cập nhật đúng trạng thái; không chạy hai lần đồng thời.
- Test orchestration bằng fake service, không cần WAV/model thật cho UI state tests.
- Full test/build/format đạt trên Windows CI.
- Publish `win-x64` self-contained chạy được khi `dotnet` không có trong `PATH`.
- Smoke test app mở được, kiểm tra fixture XML, bắt đầu/dừng an toàn và không để output dở dang.
- Không gọi kết quả Phase 05 là đã nghiệm thu Premiere; import, nghe, Translation Results và máy Windows sạch thuộc Phase 06.
