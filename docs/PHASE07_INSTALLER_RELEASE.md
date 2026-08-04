# Phase 07 — Windows installer và private release

Trạng thái: `in-progress`.

## Mục tiêu

- Tạo một installer `.exe` cho Windows 10/11 x64 từ đúng publish self-contained đã kiểm chứng.
- Cài theo người dùng vào `%LOCALAPPDATA%\Programs\Premiere Auto Dialogue XML`, không yêu cầu quyền quản trị.
- Không yêu cầu cài riêng .NET, Python, model hoặc kết nối Internet.
- Có shortcut Start Menu, tùy chọn shortcut Desktop và uninstaller chuẩn.
- Mỗi build tạo output mới, manifest SHA-256 và không ghi đè artifact cũ.
- Cài/gỡ thử trong thư mục cô lập trước khi phát hành private.

## Công nghệ và giới hạn

- Compiler: Inno Setup 7 x64.
- Installer chỉ nhận payload từ script publish `win-x64` đã kiểm tra apphost, .NET/CoreCLR, WPF, ONNX Runtime, model và giấy phép.
- Pilot ban đầu chưa ký số vì dự án chưa có code-signing certificate; Windows SmartScreen có thể cảnh báo nhà phát hành không xác định.
- Nếu sản phẩm được dùng thương mại, chủ dự án phải kiểm tra và mua commercial license Inno Setup theo điều khoản hiện hành trước phát hành thương mại.

## Cổng nghiệm thu

- Script installer có thể build lặp lại trên máy phát triển và CI Windows.
- Installer từ chối Windows không tương thích và chỉ cài payload x64.
- Silent install vào thư mục cô lập đạt; mọi file payload sau cài khớp manifest.
- App đã cài mở được; uninstaller xóa app/shortcut nhưng không xóa XML, WAV hoặc output người dùng.
- Installer, manifest và SHA-256 được giữ ngoài Git; Git chỉ chứa source build và báo cáo đã làm sạch.

## Bằng chứng ngày 2026-08-04

### Build cục bộ ứng viên 0.1.0

- Compiler: Inno Setup `7.0.2` x64.
- Installer PE x64, cài theo người dùng, không yêu cầu quyền quản trị.
- Kích thước: `52,777,941` byte.
- SHA-256: `24CE953A67A6689D5CAE52704746BC5A8E09E5C1F0A2AC9F91A6FCB038182A55`.
- Chữ ký số: chưa có (`NotSigned`). Không được mô tả bản này là đã ký hoặc không thể bị phần mềm bảo mật cảnh báo.
- Payload: `410` tệp self-contained `win-x64`, gồm .NET runtime, WPF, ONNX Runtime, model Silero VAD và giấy phép; không chứa Python.
- Test cài/gỡ cô lập đạt:
  - silent install trả mã `0`;
  - toàn bộ `410/410` tệp sau cài khớp size và SHA-256 trong manifest;
  - app đã cài mở được và có cửa sổ chính;
  - silent uninstall trả mã `0`;
  - payload và registry uninstall theo người dùng được xóa;
  - tệp mô phỏng output do người dùng tạo vẫn còn sau gỡ cài đặt.
- Kiểm tra trực quan các trang Thông tin, Chọn thư mục, Tác vụ bổ sung và Sẵn sàng cài đặt đạt; các chuỗi chính đã hiển thị tiếng Việt.

### CI trước lần Việt hóa cuối

- GitHub Actions run `30879239391` đạt toàn bộ restore, build, `93/93` test, publish self-contained, tải và xác minh attestation Inno Setup, build, cài, mở và gỡ installer.
- Artifact CI dùng compiler `7.0.2`, có `410` tệp payload và báo cáo installer `passed=true`.
- Workflow sau đó được sửa để artifact tải xuống chứa trực tiếp bốn tệp release, không còn lồng thư mục run nội bộ. Cần CI mới xác nhận thay đổi này trước khi bàn giao.

### Sự cố đã sửa trong bộ kiểm tra

Phiên bản đầu của test harness duyệt sai đường dẫn registry uninstall và có thể để lại đúng một khóa thử nghiệm sau khi test dừng. Khóa đó đã được xác minh thuộc thư mục test rồi xóa. Harness hiện chỉ dùng AppId ổn định của sản phẩm, có bước phục hồi khi lỗi và kiểm tra khóa theo người dùng đã được xóa. Đây là lỗi của bộ test, không phải lỗi gỡ cài đặt của ứng dụng.

## Cổng còn lại

- GitHub Actions phải đạt với source Việt hóa cuối và artifact đã làm phẳng.
- Người dùng cài, mở, phân tích và gỡ bản installer trên máy Windows 10 x64 sạch. Việc app dạng ZIP đã chạy trên Windows 10 trước đó không thay thế cổng kiểm tra installer này.
- Sau cổng Windows 10: cập nhật trạng thái phase, merge bằng merge commit và tạo private draft release `v0.1.0-rc.1`.
