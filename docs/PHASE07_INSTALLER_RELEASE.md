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

