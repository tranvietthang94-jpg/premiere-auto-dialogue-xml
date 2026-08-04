# Chữ ký tự ký cho phát hành nội bộ

## Phạm vi

Chữ ký này chỉ dùng cho máy nội bộ do chủ dự án kiểm soát. Chứng thư tự ký không được Windows tin cậy mặc định và không thay thế chứng thư từ CA công cộng. Không dùng gói này để phát hành công khai.

Chữ ký Authenticode giúp Windows kiểm tra installer/app có bị thay đổi sau khi ký hay không. Máy đích chỉ hiện chữ ký hợp lệ sau khi tin cậy đúng chứng thư công khai.

## Nhận diện chứng thư

- Subject: `CN=Premiere Auto Dialogue XML Internal`
- Thumbprint: `025AEBC4AA90E0D5F85658082952A7E029CF467A`
- SHA-256 file `.cer`: `105EAA41A6D4C757A84C7867938ABFF2DB286B82D4940618038A324C1E7F7221`
- Hiệu lực đến ngày 04/08/2031

Nếu bất kỳ giá trị nào khác, không cài chứng thư và không chạy installer.

## Tin cậy trên máy Windows nội bộ

Thực hiện cho từng tài khoản Windows dùng ứng dụng:

1. Mở `PremiereAutoDialogueXml-Internal-CodeSigning.cer`.
2. Chọn **Install Certificate…** rồi chọn **Current User**.
3. Chọn đặt chứng thư thủ công vào **Trusted Root Certification Authorities** và hoàn tất cảnh báo tin cậy.
4. Mở lại file `.cer`, cài thêm một lần vào **Trusted Publishers** của Current User.
5. Mở Properties của installer → **Digital Signatures**. Tên signer phải là `Premiere Auto Dialogue XML Internal` và Windows phải báo chữ ký hợp lệ.

Chỉ cài file `.cer`; không sao chép hoặc yêu cầu file PFX/private key trên máy chạy ứng dụng.

## Giới hạn

- SmartScreen có thể vẫn dùng reputation riêng ngoài việc kiểm tra chữ ký.
- Bản ký nội bộ không có timestamp công cộng. Phải ký lại trước ngày hết hạn chứng thư.
- Khóa riêng hiện không export được và chỉ tồn tại trong Windows Certificate Store của profile phát triển. Nếu profile/máy này mất, phải tạo chứng thư mới và tin cậy lại trên các máy nội bộ.
- Khi ngừng dùng chứng thư, xóa thumbprint trên khỏi **Trusted Root Certification Authorities** và **Trusted Publishers** của Current User.

## Build nội bộ

Tạo chứng thư mới chỉ khi chưa có identity nội bộ:

```powershell
.\scripts\new-internal-code-signing-certificate.ps1 `
  -OutputDirectory .\private-artifacts\phase07-internal-certificate `
  -TrustForCurrentUser
```

Build signed installer bằng thumbprint đã khóa:

```powershell
.\scripts\build-installer.ps1 `
  -OutputRoot .\artifacts\installer-signed `
  -SigningCertificateThumbprint 025AEBC4AA90E0D5F85658082952A7E029CF467A
```

GitHub Actions không nhận khóa riêng và không tạo signed release.
