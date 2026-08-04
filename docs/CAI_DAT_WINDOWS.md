# Cài Premiere Auto Dialogue XML trên Windows

## Trước khi cài

Gói cài chỉ dành cho Windows 10/11 x64. Ứng dụng đã chứa sẵn .NET runtime, ONNX Runtime và model nên không cần cài Python, không cần Internet và không cần quyền quản trị.

Bản nội bộ `0.1.0` dùng chữ ký tự ký. Trước lần cài đầu tiên trên mỗi tài khoản Windows, cần tin cậy đúng file `.cer` đi kèm theo [hướng dẫn chữ ký nội bộ](INTERNAL_CODE_SIGNING.md). Nếu chưa tin cậy chứng thư, Windows vẫn có thể hiển thị cảnh báo nhà phát hành không xác định.

Chỉ dùng tệp lấy từ private release của repository và đối chiếu SHA-256 với checksum đi kèm trước khi chạy.

## Cài đặt

1. Giữ nguyên toàn bộ tệp trong thư mục release, gồm installer, manifest, checksum, chứng thư công khai và hướng dẫn.
2. Mở installer `PremiereAutoDialogueXml-Setup-0.1.0-win-x64.exe`.
3. Đọc trang thông tin, chọn **Tiếp tục**, giữ thư mục mặc định rồi chọn **Cài đặt**.
4. Chọn **Hoàn tất** để mở ứng dụng.

Ứng dụng mặc định được cài vào:

```text
%LOCALAPPDATA%\Programs\Premiere Auto Dialogue XML
```

## Dữ liệu và gỡ cài đặt

Installer và uninstaller chỉ quản lý tệp chương trình cùng shortcut. XML, WAV, project Premiere, audit và thư mục output do người dùng chọn không bị sửa hoặc xóa.

Có thể gỡ ứng dụng trong **Settings → Apps → Installed apps → Premiere Auto Dialogue XML**. Sau khi gỡ, dữ liệu công việc vẫn được giữ nguyên.

## Giới hạn cần nhớ

- Mức `-6 dBFS` là sample peak theo từng cụm lời sau profile routing Premiere đã kiểm chứng.
- Đây không phải LUFS, true peak, limiter hoặc bảo đảm cho toàn bộ Master bus.
- Luôn import XML kết quả thành sequence mới và nghe kiểm tra nội dung trước khi dựng tiếp.
