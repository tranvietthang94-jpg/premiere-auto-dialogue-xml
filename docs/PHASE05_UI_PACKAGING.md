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
- Script publish từ chối tạo ZIP nếu thiếu apphost, .NET/CoreCLR, WPF, ONNX Runtime native, thông báo giấy phép hoặc nếu checksum model nguồn khác giá trị đã duyệt.
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

## Bằng chứng hiện tại

- `dotnet format --verify-no-changes`: đạt.
- `dotnet build -c Release`: đạt, 0 warning/0 error.
- `dotnet test -c Release`: 82/82 test đạt; gồm state machine success/cancel/failure/concurrency, diagnostic transaction và smoke test mở/đóng cửa sổ trên STA thread.
- Mở thật bản build đã phát hiện và sửa binding `ProgressBar` hai chiều vào property chỉ đọc; sau sửa, cả bản build và executable self-contained đều mở/đóng đúng.
- Kiểm tra trực quan: đủ bốn bước, trạng thái nút ban đầu đúng, cuộn được, phần kỹ thuật mở được và hộp chọn XML mở/hủy không tạo output.
- Publish local `win-x64`:
  - 410 payload file; runtimeconfig chứa .NETCore + WindowsDesktop self-contained.
  - Không Python, PDB hoặc installer; có ONNX Runtime native, third-party notice và license Silero.
  - ZIP 71.101.296 byte, 411 entry tính cả manifest.
  - ZIP SHA-256 `DCD2EC940873DF6A6811EC1981D7AFEFD2E7321A0CC5E2BFFF32BF9C4BC5D837`.
  - Manifest ghi `installer=false`, model 6.2.1 và checksum `1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3`.

Artifact local nằm trong `artifacts/` bị Git ignore. CI phải chạy lại test và script publish trên Windows trước khi Phase 05 được merge.
