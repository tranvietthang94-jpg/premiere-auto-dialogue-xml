# Phase 12 — Tương thích input Premiere theo từng cổng

Trạng thái: `opened; objective and gates locked` ngày 2026-08-14. Phase được mở từ clean `main` commit `3f41d7cd2d8b4d90241b4bd2f50f798e23d65796` trên branch `codex/phase12-premiere-input-compatibility`. Phase 11 calibrated multi-mic bleed đã được giữ ở nhánh nghiên cứu và không merge; Phase 12 không mang code, audit `1.7` hoặc quyết định shadow của Phase 11 sang.

## Quyết định sản phẩm

Phase 12 mở rộng số project Premiere hợp lệ mà app có thể xử lý, nhưng không thay đổi cốt lõi MVP: phân tích âm thanh hoàn toàn offline, giữ lời rõ, chỉ Disable noise/bleed rõ ràng, giữ vùng mơ hồ Enabled và xuất một XML mới có thể kiểm tra. Không thêm preview, playback hoặc editor.

Cổng adoption đầu tiên chỉ dành cho sequence nguyên frame rate `24`, `25` và `30 fps` NDF với master stereo, source mono PCM `48 kHz` và routing `mono-center-equal-power-to-stereo` hiện hành. `25 fps` là baseline bắt buộc; `24/30 fps` là khả năng mới.

Stereo source, sample rate khác `48 kHz`, routing profile mới, `23,976/29,97`, drop-frame, effect, automation, submix, nested sequence, multicam, retime và overlap cùng track không thuộc cổng đầu tiên. Chúng tiếp tục bị từ chối rõ ràng; không tự downmix, resample hoặc đoán routing.

## Mục tiêu

1. Thay mọi giả định `25 fps` rải rác bằng một frame-grid duy nhất lấy từ project đã kiểm tra.
2. Chấp nhận an toàn `24/25/30 fps` NDF xuyên suốt inspector → source range → analysis → phrase/frame alignment → marker/review → XML writer → validator.
3. Giữ nguyên sample-level VAD/noise/bleed/gain/routing, model Silero `6.2.1`, target hậu routing `-6 dBFS` và boost tối đa `+18 dB`.
4. Chứng minh `25 fps` không đổi semantic trước khi nhận `24/30 fps`.
5. Chỉ adopt từng frame rate sau fixture, XML import, PCM stem và Final Cut Pro XML re-export từ đúng candidate.

## Baseline và khoản nợ đã xác định

- `PremiereXmlInspector` hiện chỉ nhận sequence `25 fps`; clip rate phải bằng sequence và `ntsc=TRUE` bị từ chối.
- `AudioMath` hiện khóa `48.000 / 25 = 1.920` sample/video-frame.
- `TrackDialogueAnalyzer` dùng hằng số sample/frame để dựng evidence và căn frame.
- `PremiereXmlGenerator` khóa `25 fps/48 kHz`, dùng `1.920` sample/frame khi split fragment, chọn peak frame-safe và đặt marker.
- `PcmTrackPeakValidator` khóa `25 fps` và `1.920` sample/frame.
- Review/audit đã ghi `FrameRate` và timecode `sequence-relative-ndf`, nhưng cần regression riêng cho `24/30`.
- Với `48 kHz`, ba rate đầu tiên đều có frame-grid nguyên: `24 → 2.000`, `25 → 1.920`, `30 → 1.600` sample/frame. Phase này không dùng phép xấp xỉ fractional-rate.
- Baseline clean `main` đạt Release `153/153` test ngày 2026-08-14 trước khi có thay đổi production Phase 12.

## Hợp đồng an toàn không được đổi

- Không sửa hoặc ghi đè XML, WAV hay `.prproj` gốc; mỗi lần chạy tạo output/audit mới.
- Speech và Ambiguous cuối luôn Enabled. M19/A3 frame `11214–11218` của HGE2 phải tiếp tục Enabled ở regression `25 fps`.
- Gain phrase vẫn dùng peak lớn hơn giữa lõi direct speech và toàn bộ frame Enabled liên quan; target hậu routing, compensation `+3,0102999566 dB`, cap `+18 dB` và encoding XML Phase 00 giữ nguyên.
- Writer chỉ chạy sau validator. Rate, sample rate, source range, pproTicks, routing hoặc analysis provenance không nhất quán phải dừng trước khi tạo output.
- Không dùng PCM/re-export của XML cũ để chứng minh một XML candidate mới.

## Chính sách rate ban đầu

- `25 fps`: giữ tương thích với XML hiện tại có `ntsc` vắng mặt hoặc `FALSE`.
- `24/30 fps`: cổng đầu tiên yêu cầu `<ntsc>FALSE</ntsc>` rõ ràng ở sequence và clip.
- Sequence và mọi clip phải cùng timebase/NDF; bất đồng bị từ chối như retime hoặc rate không hỗ trợ.
- `ntsc=TRUE`, timebase khác `24/25/30`, fractional-rate và drop-frame tiếp tục fail closed.
- XML output clone và giữ nguyên rate metadata đã xác nhận; không rewrite `24` thành `25` hoặc `30`.

## Kế hoạch theo slice

### Slice 12A — rate contract và synthetic fixtures

- Tạo kiểu/value object duy nhất mô tả rate NDF và sample-per-frame nguyên.
- Mở rộng fixture inspector cho `24/25/30`, explicit/missing `ntsc`, clip-rate mismatch và rate bị từ chối.
- Khóa conversion frame ↔ sample ↔ pproTicks bằng test biên, số lớn, source trim, gap và lệch subframe hợp lệ.
- Chưa mở writer/app production cho rate mới ở cuối 12A.

### Slice 12B — analysis frame-grid

- Truyền frame-grid từ project vào analyzer thay vì dùng hằng `25 fps`.
- Kiểm frame evidence, phrase qua ranh giới WAV, padding, overlap comparison và frame-safe peak ở cả ba rate.
- Cùng PCM/timeline tương đương phải cho quyết định sample-level tương đương; khác biệt chỉ được phát sinh từ biên video-frame đã khai báo và phải có report.

### Slice 12C — XML writer, review, audit và validator

- Parameterize split fragment, marker, review timecode và PCM validator bằng rate đã xác nhận.
- XML output phải giữ đúng sequence/clip rate, pproTicks, source range, track layout, routing, video và media reference.
- Validator tái tính mọi boundary/gain từ cùng frame-grid và từ chối rate/provenance bị sửa.
- Nếu schema audit cần đổi, chỉ tăng version một lần cùng migration/test rõ ràng.

### Slice 12D — regression 25 fps và pilot local

- Toàn bộ test Release, format, diff check, self-contained publish và installer smoke đạt.
- HGE2 và full HGE `25 fps` phải có coverage mismatch `0`, lost Enabled `0`, M19 Enabled, không đổi status/gain/marker/XML semantic so với Phase 10.
- Runtime full HGE dưới `90 phút`, peak RAM dưới `1,5 GB`, temporary file count `0`.

### Slice 12E — Premiere adoption 24/30 fps

- Mỗi rate có một fixture Premiere riêng với source trim, gap, clip nối tiếp, phrase qua biên WAV, Disable và gain đại diện.
- Import đúng candidate XML; xác nhận duration, timing, media relink, track/routing và marker.
- Export PCM mono `48 kHz` từ đầu sequence cho từng track cần đo. Phrase không cap phải đạt `-6,0 ±0,1 dBFS`; phrase cap khớp predicted peak và không nóng hơn target.
- Re-export Final Cut Pro XML và so semantic timing/source/Enabled/gain/marker với đúng candidate.
- Chỉ rate vượt toàn bộ cổng mới được thêm vào phạm vi sản phẩm. Một rate thất bại không kéo rate còn lại vào adoption.

### Cổng sau — stereo source, sample rate và routing

Chỉ mở sau khi 12E đóng. Mỗi profile mới phải có channel mapping do operator xác nhận, phép đo peak đúng routing, fixture và Premiere round-trip riêng. Không gộp stereo source hoặc resampling vào thay đổi frame rate.

## Cổng nghiệm thu

### Tính đúng

- Frame/sample/ticks conversion không overflow và đúng tại đầu/cuối clip cho `24/25/30 fps`.
- Parser từ chối rate mơ hồ trước media read và không suy diễn NDF/DF.
- Analysis, writer và validator dùng cùng một frame-grid; không còn hằng `25 fps` trong đường production cần đa rate.
- XML nguồn `25 fps` hiện hành giữ nguyên semantic audio và bằng chứng Phase 10.

### Premiere

- Fixture `24 fps NDF` và `30 fps NDF` đều import, phát và re-export được.
- Timing/source trim/pproTicks, Enabled/Disabled, gain và marker nằm trong tolerance hiện hành.
- PCM chứng minh target hậu routing bằng đúng XML hash candidate.

### Safety

- Input hash không đổi; lỗi/cancel không để output dở.
- Không tự downmix, resample hoặc chọn routing.
- Không nới lỏng speech/ambiguous preservation, M19 hoặc XML publication validator.
- Không thay RC1 cho tới khi có quyết định release riêng.

## Điều kiện dừng hoặc rollback

- Nếu phải đoán rate, routing hoặc channel map để tiếp tục, dừng và giữ profile đó unsupported.
- Nếu `25 fps` có bất kỳ lost Enabled, gain/timing drift hoặc M19 regression nào, rollback thay đổi đa rate.
- Nếu Premiere normalize `24/30` làm timing/gain/marker vượt tolerance, không adopt rate đó dù synthetic đạt.
- Nếu runtime/RAM vượt cổng, tối ưu implementation; không bỏ validator hoặc provenance.

## Bước tiếp theo

Thực hiện Slice 12A: tạo rate contract dùng chung và synthetic fixture/test cho `24/25/30 fps NDF`, nhưng vẫn giữ app production chỉ nhận `25 fps` cho tới khi analysis/writer/validator được parameterize ở các slice sau.
