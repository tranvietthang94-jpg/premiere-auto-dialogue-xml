# Premiere Auto Dialogue XML

Ứng dụng Windows chạy hoàn toàn offline để chuẩn bị các line tiếng nghệ sĩ trước khi dựng trong Adobe Premiere Pro.

Ứng dụng nhận một tệp Final Cut Pro XML do Premiere xuất, đọc đúng các đoạn WAV đang nằm trên timeline, nhận diện lời thoại, tắt vùng noise/bleed rõ ràng và cân sample peak từng cụm thoại gần `-6 dBFS` sau routing mono-center đã xác nhận của Premiere. Kết quả là một XML mới để import ngược vào Premiere; XML, WAV và project gốc luôn được giữ nguyên.

## Phạm vi MVP

- Một sequence 25 fps, stereo master.
- Nhiều track nghệ sĩ; mỗi track có thể gồm nhiều WAV mono PCM 48 kHz nối tiếp.
- Hỗ trợ source trim, track bắt đầu lệch nhau và lời đi qua ranh giới hai WAV.
- Không hỗ trợ effect, automation, submix, nested sequence, multicam, retime hoặc media thiếu.
- Vùng lời được giữ và cân peak; noise/bleed rõ ràng được Disable; vùng mơ hồ được giữ và đánh dấu.
- Không Python, cloud, API, telemetry hoặc tải model sau khi cài.

## Nguyên tắc an toàn

- Không sửa hoặc ghi đè input.
- Không commit media thật, project Premiere, log riêng tư hoặc output cục bộ.
- Chỉ tạo XML cuối khi toàn bộ kiểm tra và phân tích đã hoàn tất.
- `-6 dBFS` là sample peak từng cụm lời sau profile routing `mono-center-equal-power-to-stereo`; app bù `+3,0103 dB` nhưng boost tổng vẫn giới hạn `+18 dB`.
- Đây không phải LUFS, true peak, limiter hoặc cam kết cho toàn bộ Master bus.

Kế hoạch triển khai và tiêu chí nghiệm thu nằm trong [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).

## Trạng thái

Phase 00–04 đã hoàn tất compatibility gate, nền tảng WPF, parser XML/WAV, phân tích Silero VAD/gain/bleed và XML/audit writer giao dịch. Phase 05 đã nối luồng bốn bước tiếng Việt, tiến độ, dừng an toàn, diagnostic có giới hạn và publish self-contained `win-x64`; installer và nghiệm thu Premiere thực tế vẫn thuộc Phase 06.

Bằng chứng theo phase nằm trong thư mục [docs](docs).

## Build dành cho phát triển

Yêu cầu .NET SDK `10.0.302` trên Windows:

```powershell
dotnet restore PremiereAutoDialogueXml.slnx
dotnet build PremiereAutoDialogueXml.slnx --configuration Release --no-restore
dotnet test PremiereAutoDialogueXml.slnx --configuration Release --no-build
```

SDK cài cục bộ trong `.tools/` được Git bỏ qua. CI chạy cùng phiên bản SDK trên `windows-latest`.

Chạy app phát triển:

```powershell
dotnet run --project src/PremiereAutoDialogueXml.App --configuration Release
```

Tạo publish folder và ZIP self-contained mới, không ghi đè artifact cũ:

```powershell
.\scripts\publish-win-x64.ps1
```

Script kiểm runtime, ONNX native, license, Python/PDB và tạo `publish-manifest.json` chứa SHA-256 payload. ZIP Phase 05 chưa phải installer và chưa được coi là bản phát hành pilot.
