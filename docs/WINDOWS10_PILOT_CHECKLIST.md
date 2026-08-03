# Checklist thử trên Windows 10 x64

Mục tiêu: xác nhận gói portable mở, phân tích và xuất kết quả trên Windows 10. Không cần cài Premiere, .NET hoặc Python cho lượt thử này.

## Chuẩn bị

1. Dùng ZIP candidate mới nhất có SHA-256 `5C01D2E7DA7E44BCD9A76E1879D22FE25F1FF5E3EF1861C07E6BA34A67DDF900`.
2. Chép theo một XML thử và toàn bộ WAV mà XML tham chiếu. Với HGE2, giữ cấu trúc/ổ đĩa giống máy nguồn nếu XML chứa đường dẫn tuyệt đối.
3. Mở `winver` và ghi lại edition cùng version/build Windows 10.
4. Giải nén ZIP hoàn toàn vào một thư mục mới; không chạy app trực tiếp bên trong ZIP.

## Thao tác

1. Chạy `PremiereAutoDialogueXml.exe`.
2. Chọn XML và thư mục output mới.
3. Bấm **Kiểm tra**; xác nhận không báo thiếu media/runtime/model.
4. Bấm **Phân tích** và chờ hoàn tất.
5. Xác nhận thư mục run mới chứa cả `*_AutoAudio.xml` và `*_AutoAudio.audit.json`.
6. Đóng và mở lại app một lần để xác nhận khởi động ổn định.

## Bằng chứng tối thiểu cần báo lại

- Edition, version và OS build từ `winver`.
- App có mở được không.
- Kiểm tra XML có đạt không.
- Phân tích có hoàn tất và tạo đủ XML/audit không.
- Nội dung thông báo lỗi nếu có; không cần gửi media thật.

Nếu SmartScreen xuất hiện vì pilot chưa ký số, đối chiếu đúng SHA-256 ở trên trước khi chọn chạy. Không tắt Windows Defender.
