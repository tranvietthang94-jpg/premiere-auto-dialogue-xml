# Phase 01 — nền tảng ứng dụng

## Phạm vi

Phase 01 tạo khung ứng dụng Windows và các hợp đồng nền tảng; chưa parse FCP XML, chưa đọc WAV và chưa chạy VAD.

- Solution .NET 10 gồm WPF app, Core thuần C# và MSTest.
- UI tiếng Việt giữ luồng chính bốn bước: chọn XML → kiểm tra → phân tích → xuất kết quả.
- Chọn XML và thư mục output bằng dialog chuẩn của Windows.
- Kiểm tra an toàn mức nền tảng: đường dẫn có giá trị, `.xml`, file tồn tại/không rỗng và thư mục output tồn tại.
- Preset “Cân bằng” khóa toàn bộ threshold đã duyệt.
- Core không phụ thuộc WPF để parser/audio engine ở phase sau có thể test độc lập.
- GitHub Actions chạy restore, build và test trên `windows-latest` với .NET SDK 10.0.302.

## Nguyên tắc chưa thay đổi

- Không sửa input hoặc tạo output khi mới chọn file.
- Không báo “XML hợp lệ” trước khi parser Phase 02 kiểm tra nội dung và media.
- Không thêm model, telemetry, cloud, Python hoặc installer ở phase này.
- `.tools/` chỉ chứa SDK cục bộ phục vụ build và bị Git bỏ qua.

## Cổng nghiệm thu

- `dotnet restore`, `dotnet build -c Release` và `dotnet test -c Release` đạt.
- WPF app khởi động trên Windows và bốn bước hiển thị đúng tiếng Việt.
- Chọn file Unicode và thư mục output hoạt động; XML rỗng/sai extension/không tồn tại hoặc thư mục output không tồn tại không được chuyển sang bước kiểm tra.
- CI của draft PR đạt trước khi merge.

## Bằng chứng cục bộ

Ngày 2026-08-02 trên Windows:

- Restore: đạt.
- Build Release: đạt, `0` warning và `0` error.
- MSTest: `7/7` đạt.
- WPF launch: đạt sau khi build Release.
- Dialog XML: chọn thành công `F:\demo\test HGE2.xml`.
- Dialog output: chọn thành công `F:\demo`.
- Trạng thái UI: Bước 1 “Đã chọn”, Bước 2 “Đã kiểm tra nền tảng”.
- Chi tiết kỹ thuật hiển thị đúng VAD `0.50`, `120/350 ms`, padding `200/300 ms`, peak `-6 dBFS`, boost `+18 dB`, bốn worker và giới hạn không cloud/API/telemetry.
- Không tạo XML/WAV/output khi mới kiểm tra lựa chọn.
