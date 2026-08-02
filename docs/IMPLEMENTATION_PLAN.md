# Kế hoạch triển khai

## GitHub và workflow

Repository private: `tranvietthang94-jpg/premiere-auto-dialogue-xml`.

Mỗi phase dùng branch và draft pull request riêng. Các thay đổi có ý nghĩa được commit và push thường xuyên; chỉ merge vào `main` sau khi test của phase đạt.

1. `phase/00-premiere-xml-compatibility`
2. `phase/01-app-foundation`
3. `phase/02-xml-media-parser`
4. `phase/03-audio-analysis`
5. `phase/04-xml-writer`
6. `phase/05-ui-packaging`
7. `phase/06-pilot-validation`

## Kiến trúc

- .NET 10 LTS, C#, WPF, self-contained `win-x64`.
- Silero VAD ONNX chạy CPU bằng Microsoft.ML.OnnxRuntime.
- Giao diện tiếng Việt: chọn XML, kiểm tra, phân tích, xuất kết quả.
- Streaming I/O với offset 64-bit; tối đa bốn worker; không nạp toàn bộ WAV vào RAM.
- Output mỗi lần chạy: XML mới, audit JSON và log chẩn đoán có giới hạn khi thất bại.

## Hợp đồng input

- Một sequence 25 fps, stereo master.
- Track đơn giản không effect/automation/submix/nesting/multicam/retime.
- WAV mono PCM 48 kHz, 16/24/32-bit, có thể gồm nhiều file nối tiếp trên một track.
- Cho phép source `in/out` khác 0, staggered tracks và gaps.
- Từ chối overlap trên cùng track hoặc media không thể đối chiếu.
- Header WAV là nguồn sự thật cho sample rate, bit depth và channel count.

## Preset Cân bằng

- VAD threshold: `0.50`.
- Speech tối thiểu: `120 ms`.
- Khoảng nghỉ tách câu: `350 ms`.
- Padding trước/sau: `200/300 ms`.
- Direct voice tối thiểu: `10 dB` trên adaptive noise floor.
- Bleed: mic khác cao hơn `12 dB`, correlation từ `0.80`, lag tối đa `12 ms`, đồng thời mic đích thấp hơn primary reference.
- Vùng mơ hồ luôn được giữ và đánh dấu.

## Gain và XML output

- Đo sample peak trên vùng lời xác nhận.
- `gainDb = -6 - measuredPeakDbFS`, boost tối đa `+18 dB`.
- Speech dùng fragment Enabled cùng Audio Levels tĩnh.
- Noise/bleed dùng fragment Disabled, không xóa khỏi timeline.
- Clone video, sequence/track metadata và media references; tạo UUID/clip IDs mới.
- Cập nhật chính xác `start/end`, `in/out`, `pproTicksIn/Out` và source-track.
- Gain trên `+12 dB` chỉ dùng hai Audio Levels sau khi Phase 00 chứng minh Premiere cộng đúng.

## Phase 00 compatibility gate

Tạo duplicate sequence nhỏ có các ca `-6`, `+6`, `+12`, `+18 dB`, hai Audio Levels chồng nhau, Disable và source trim. Export/import XML và export PCM để so timing/peak.

Nếu Disable, timing hoặc peak sai quá `0.1 dB`, dừng tại cổng này; không âm thầm cap `+12 dB` hoặc render WAV thay thế.

## Nghiệm thu

- Synthetic tests cho XML, PCM, VAD, noise, bleed, overlap và file boundary.
- `F:\demo\test HGE2.xml`: source trim và bảy track thực tế.
- `F:\demo\test HGE.xml`: 19 WAV nối tiếp và xử lý dài.
- 100% direct-speech interval đã gắn nhãn được giữ; ít nhất 90% noise/bleed rõ ràng được Disable; ambiguous không bị Disable.
- Câu không bị cap đạt `-6.0 ± 0.1 dBFS` sau Premiere round-trip.
- Mục tiêu trên i9-12900K: HGE2 dưới 15 phút, full HGE dưới 90 phút, RAM dưới 1.5 GB.
- Bộ cài chạy trên Windows 10/11 x64 sạch, không Python và không Internet.

