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
- `-6 dBFS` là sample peak từng cụm lời sau profile routing `mono-center-equal-power-to-stereo`; app dùng peak lớn hơn giữa lõi lời trực tiếp và các frame phrase thực sự được bật, bù `+3,0103 dB`, nhưng boost tổng vẫn giới hạn `+18 dB`.
- Đây không phải LUFS, true peak, limiter hoặc cam kết cho toàn bộ Master bus.

Kế hoạch triển khai và tiêu chí nghiệm thu nằm trong [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).

## Trạng thái

Phase 00–10 đã hoàn tất về logic và bằng chứng bắt buộc. Phase 09 thêm resampler chống alias, shadow gate bảo thủ và validator trước XML. Phase 10 thêm estimator background-eligible, VAD start/continue hysteresis, project merge bảo thủ hai tầng, audit `1.6` có giới hạn bộ nhớ và XML compaction theo semantic audio; merge qua PR `#14` tại `19c6577`, CI hậu merge `31691393143` đạt. Synthetic, HGE2/full HGE, Premiere PCM A1–A7, M19, Final Cut Pro XML re-export và CI/installer đều đạt; so với Phase 09, coverage lệch `0` và không mất bất kỳ frame Enabled nào. Candidate HGE2 cuối có XML SHA-256 `6F855699ED6D39712F3118A661DCF18943750F805D3EDB662546D033A808910E`.

Phase 12 đã mở từ clean `main` để mở rộng input Premiere theo từng cổng. Cổng đầu chỉ nghiên cứu sequence `24/25/30 fps NDF` với source mono PCM `48 kHz`, master stereo và routing hiện hành; stereo source, sample rate/routing mới, fractional-rate và drop-frame vẫn bị từ chối. Phase không thay logic speech/noise/bleed/gain hoặc RC1. Xem [docs/PHASE09_AUDIO_XML_HARDENING.md](docs/PHASE09_AUDIO_XML_HARDENING.md), [docs/PHASE10_NOISE_BOUNDARY_STABILITY.md](docs/PHASE10_NOISE_BOUNDARY_STABILITY.md) và [docs/PHASE12_PREMIERE_INPUT_COMPATIBILITY.md](docs/PHASE12_PREMIERE_INPUT_COMPATIBILITY.md). Cổng nhãn nghe Mục tiêu được chủ dự án miễn, nên dự án không tuyên bố các tỷ lệ 100%/90%/0% chưa đo.

## Hệ điều hành mục tiêu

- Windows 11 x64.
- Windows 10 x64. [.NET 10 hiện được Microsoft hỗ trợ trên Windows 10 LTSC/Enterprise còn trong vòng đời](https://learn.microsoft.com/en-us/dotnet/core/install/windows); các bản Home/Pro đã hết vòng đời chỉ được coi là tương thích best-effort và vẫn phải qua test thực tế.
- Bản phát hành là self-contained, không yêu cầu người dùng cài riêng .NET hoặc Python.

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

Script kiểm apphost, .NET/CoreCLR, WPF, ONNX native, model checksum, license, Python/PDB và tạo `publish-manifest.json` chứa SHA-256 payload. ZIP pilot chưa phải installer.

Tài liệu cài đặt nằm tại [docs/CAI_DAT_WINDOWS.md](docs/CAI_DAT_WINDOWS.md). Quy trình tự ký nội bộ và giới hạn tin cậy nằm tại [docs/INTERNAL_CODE_SIGNING.md](docs/INTERNAL_CODE_SIGNING.md).
