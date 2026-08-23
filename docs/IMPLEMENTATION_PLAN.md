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
8. `phase/07-installer-release`
9. `phase/08-review-queue`
10. `phase/09-audio-xml-hardening`
11. `codex/phase10-noise-boundary-stability`
12. `codex/phase11-calibrated-multimic-bleed` — research-only, đóng không merge
13. `codex/phase12-premiere-input-compatibility`
14. `codex/phase13-reliability-performance`

Phase 00–09 đã hoàn tất và merge vào `main`. Theo quyết định sản phẩm ngày 2026-08-09, app không mở nhánh preview/review UI; Phase 09 tập trung làm chắc đầu vào VAD, safety validator và XML output. Phase 09 đạt mọi gate HGE2/full HGE, Premiere PCM A1–A7, Final Cut Pro XML re-export và CI/packaging; merge qua PR `#12` tại `50ee4f9`, CI hậu merge `31461869682` đạt. Xem [PHASE09_AUDIO_XML_HARDENING.md](PHASE09_AUDIO_XML_HARDENING.md).

Phase 10 đã đạt toàn bộ gate và merge qua PR `#14` tại `19c6577`; CI hậu merge `31691393143` đạt. Estimator background-eligible, start/continue hysteresis, shadow/fail-safe, audit `1.6` và output compaction qua synthetic, HGE2/full HGE với coverage mismatch `0` và mất Enabled `0`; Premiere PCM A1–A7/M19/XML re-export cùng CI/installer đều đạt. Phase không đổi model, gain/routing hoặc UI sản phẩm và không tạo release mới. Xem [PHASE10_NOISE_BOUNDARY_STABILITY.md](PHASE10_NOISE_BOUNDARY_STABILITY.md).

Phase 11 calibrated multi-mic bleed đã hoàn tất như nghiên cứu shadow-only trên branch riêng nhưng không được adopt/merge vì hai corpus thật không tạo stable calibration hoặc lợi ích chất lượng, trong khi tăng audit/RAM. `main`, RC1 và production semantic vẫn giữ Phase 10.

Phase 12 đã đạt cổng local trên branch `codex/phase12-premiere-input-compatibility`. Cổng đầu mở rộng sequence nguyên `24/25/30 fps NDF`; `25 fps` là regression baseline. Source vẫn mono PCM `48 kHz`, master stereo và routing `mono-center-equal-power-to-stereo`. Slice 12A–12C truyền một frame-grid duy nhất qua parser, analysis, DOM/streaming writer, review, audit và validator; audit production `1.8` ghi timing provenance. Slice 12D chứng minh HGE2/full HGE 25 fps có coverage mismatch/lost Enabled/newly Enabled bằng `0`, giữ M19, đạt runtime/RAM và installer smoke. Slice 12E import/re-export thật trên Premiere Pro 2026: PCM 24/30 đạt mọi phrase; XML giữ clip, Enabled/Disabled, gain và marker. Premiere chỉ normalize sequence depth metadata `24 → 16`; ngoại lệ này được khóa explicit và không nới các trường khác. `24/25/30 fps NDF` đã được adopt; stereo source, sample rate/routing mới, fractional-rate và drop-frame là các cổng riêng chưa mở. Xem [PHASE12_PREMIERE_INPUT_COMPATIBILITY.md](PHASE12_PREMIERE_INPUT_COMPATIBILITY.md).

Phase 13 đã đạt; implementation merge PR `#18` tại `ad89d2a`, PR CI `32562437223` và hậu merge CI `32562657372` đạt mà không đổi VAD/noise/bleed/gain/routing/marker/XML semantic. CI có `8/8` policy case khóa strict/explicit sequence-depth cho cả 24/30. Paired scan đọc PCM một lần cho hai detector độc lập; audit `1.9` giữ full count/SHA-256 và mẫu bounded. HGE2 giảm RAM/audit với runtime trong `+5%`; full HGE đạt `75:52,4`/`1.218,3 MB`/audit `157,52 MB`, coverage mismatch/lost Enabled/newly Enabled `0`, M19 Enabled. Theo quyết định sản phẩm ngày 2026-08-23, GitHub chỉ lưu source code: CI build/smoke-test installer tạm thời nhưng không upload; installer dùng thật chỉ được tạo và giữ cục bộ. Xem [PHASE13_RELIABILITY_PERFORMANCE.md](PHASE13_RELIABILITY_PERFORMANCE.md).

## Kiến trúc

- .NET 10 LTS, C#, WPF, self-contained `win-x64`.
- Silero VAD ONNX chạy CPU bằng Microsoft.ML.OnnxRuntime.
- Giao diện tiếng Việt: chọn XML, kiểm tra, phân tích, xuất kết quả.
- Streaming I/O với offset 64-bit; tối đa bốn worker; không nạp toàn bộ WAV vào RAM.
- Output mỗi lần chạy: XML mới, audit JSON và log chẩn đoán có giới hạn khi thất bại.

## Hợp đồng input

- Một sequence nguyên `24/25/30 fps NDF`, stereo master; 24/30 phải ghi rõ `ntsc=FALSE` ở sequence và clip, 25 giữ tương thích với metadata cũ thiếu `ntsc`.
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

- Đo sample peak trên vùng lời xác nhận, sau đó đối chiếu peak lớn nhất của các frame phrase thực sự Enabled sau khi căn theo frame XML; dùng giá trị lớn hơn làm tham chiếu gain để padding/biên frame không làm output nóng hơn target.
- Profile routing đã xác nhận: `mono-center-equal-power-to-stereo`, suy hao `-3,0102999566 dB` khi Premiere export stem mono.
- `gainDb = -6 - gainReferencePeakDbFS + 3,0102999566`, boost tổng vẫn tối đa `+18 dB`.
- Phrase không bị cap dự kiến đạt gần `-6 dBFS` sau routing; phrase bị cap dự kiến bằng `measuredPeakDbFS + 18 - 3,0102999566`.
- Speech dùng fragment Enabled cùng Audio Levels tĩnh.
- Noise/bleed dùng fragment Disabled, không xóa khỏi timeline.
- Clone video, sequence/track metadata và media references; tạo UUID/clip IDs mới.
- Cập nhật chính xác `start/end`, `in/out`, `pproTicksIn/Out` và source-track.
- Gain không lớn hơn `+12 dB` dùng một Audio Levels với `value=10^(gainDb/20)`.
- Gain trên `+12 dB` đến `+18 dB` dùng Audio Levels `+12 dB` và Premiere Gain filter cho phần còn lại; trường `Gain(dB)/value` phải ghi hệ số `10^(remainderDb/20)`, không ghi literal dB. Phase 00 đã chứng minh encoding này đạt XML và PCM trong `±0.1 dB`.

## Phase 00 compatibility gate

Tạo duplicate sequence nhỏ có các ca `-6`, `+6`, `+12`, `+18 dB`, hai Audio Levels chồng nhau, Disable và source trim. Export/import XML và export PCM để so timing/peak.

Nếu Disable, timing hoặc peak sai quá `0.1 dB`, dừng tại cổng này; không âm thầm cap `+12 dB` hoặc render WAV thay thế.

Kết quả Premiere Pro 2026 ngày 2026-08-02: **đạt** sau vòng mở rộng Gain-filter hệ số tuyến tính. Xem [PHASE00_RESULT.md](PHASE00_RESULT.md).

## Nghiệm thu

- Synthetic tests cho XML, PCM, VAD, noise, bleed, overlap và file boundary.
- `F:\demo\test HGE2.xml`: source trim và bảy track thực tế.
- `F:\demo\test HGE.xml`: 19 WAV nối tiếp và xử lý dài.
- Nếu có tập nhãn Mục tiêu hợp lệ: 100% direct-speech interval được giữ; ít nhất 90% noise/bleed rõ ràng được Disable; ambiguous không bị Disable. Chủ dự án đã miễn cổng nhãn này cho Phase 06 ngày 2026-08-04, nên báo cáo không tuyên bố ba tỷ lệ chưa đo.
- Câu không bị cap đạt `-6.0 ± 0.1 dBFS` sau Premiere round-trip.
- Mục tiêu trên i9-12900K: HGE2 dưới 15 phút, full HGE dưới 90 phút, RAM dưới 1.5 GB.
- Gói self-contained chạy trên Windows 10/11 x64 mà không cần Python hoặc Internet. Pilot thực tế đã đạt trên Windows 11 khác và máy ảo Windows 10 x64 mới cài. Theo ma trận .NET 10 hiện hành của Microsoft, hỗ trợ upstream chính thức trên Windows 10 giới hạn ở các bản LTSC/Enterprise còn trong vòng đời; Home/Pro cũ là best-effort.
