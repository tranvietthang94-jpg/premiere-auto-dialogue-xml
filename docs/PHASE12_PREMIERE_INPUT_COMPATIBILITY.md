# Phase 12 — Tương thích input Premiere theo từng cổng

Trạng thái: `Phase 12 passed; 24/25/30 fps NDF adopted` ngày 2026-08-22. Phase được mở từ clean `main` commit `3f41d7cd2d8b4bd2f50f798e23d65796` trên branch `codex/phase12-premiere-input-compatibility`. Phase 11 calibrated multi-mic bleed đã được giữ ở nhánh nghiên cứu và không merge; Phase 12 không mang code, audit `1.7` hoặc quyết định shadow của Phase 11 sang. Regression 25 fps, Premiere PCM và Final Cut Pro XML re-export riêng cho 24/30 đều đã đạt.

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

Kết quả 12A:

- Thêm `PremiereNdfFrameGrid` làm rate contract duy nhất cho `24/25/30 fps NDF` với audio `48 kHz`; contract khóa lần lượt `2.000/1.920/1.600` sample/frame và phép đổi frame ↔ sample ↔ pproTicks không dùng phép nhân `Int64` dễ overflow.
- Test khóa whole-frame/subframe boundaries, số frame lớn, input âm, overflow, rate ngoài cổng và sample rate khác `48 kHz`.
- Inspector nhận diện `24/30 NDF` nhưng trả `sequence-rate-not-enabled` trước media read; thiếu `ntsc=FALSE` rõ ràng ở rate mới trả `ntsc-rate-required`; `ntsc=TRUE` và rate ngoài `24/25/30` tiếp tục fail closed. XML `25 fps` thiếu trường `ntsc` vẫn tương thích như baseline.
- Fixture inspector nay parameterize sequence/clip rate và khóa clip-rate mismatch. App, analyzer và writer production vẫn chỉ nhận `25 fps`.
- Toàn bộ Release đạt `178/178`; targeted rate/inspector đạt, format và diff check sạch.

### Slice 12B — analysis frame-grid

- Truyền frame-grid từ project vào analyzer thay vì dùng hằng `25 fps`.
- Kiểm frame evidence, phrase qua ranh giới WAV, padding, overlap comparison và frame-safe peak ở cả ba rate.
- Cùng PCM/timeline tương đương phải cho quyết định sample-level tương đương; khác biệt chỉ được phát sinh từ biên video-frame đã khai báo và phải có report.

Kết quả 12B:

- `AudioProjectAnalyzer` tạo một `PremiereNdfFrameGrid` từ rate/sample rate đã kiểm tra rồi truyền cùng instance contract vào scanner, timeline PCM accessor, phrase analyzer, bleed resolver và cross-track shadow.
- `TrackAudioScanner`, `TimelinePcmAccessor` và `TrackDialogueAnalyzer` không còn dùng hằng `25 fps/1.920 sample` trong đường production. Clip/gap coverage, padding media, segment boundary, frame-aligned phrase interval và peak measurement đều dùng grid của project.
- Công cụ chẩn đoán VAD cũng lấy frame/sample/timecode từ project thay vì hằng `25 fps`.
- Test chạy `AudioProjectAnalyzer` trực tiếp ở `24/25/30`, khóa timeline coverage và phrase đi qua hai WAV liền nhau trên cả ba grid. Default/invalid grid bị từ chối.
- Toàn bộ Release đạt `188/188`; solution build 0 warning/error, format và diff check sạch. Inspector và writer production vẫn khóa `24/30`, nên Slice 12B chưa tạo output đa rate.

### Slice 12C — XML writer, review, audit và validator

- Parameterize split fragment, marker, review timecode và PCM validator bằng rate đã xác nhận.
- XML output phải giữ đúng sequence/clip rate, pproTicks, source range, track layout, routing, video và media reference.
- Validator tái tính mọi boundary/gain từ cùng frame-grid và từ chối rate/provenance bị sửa.
- Nếu schema audit cần đổi, chỉ tăng version một lần cùng migration/test rõ ràng.

Kết quả 12C:

- `PremiereXmlGenerator` cho cả DOM và generation plan, `OutputDecisionContractValidator`, `ReviewGroupBuilder` và `PcmTrackPeakValidator` đều dùng `PremiereNdfFrameGrid`; không còn phép chia/hằng `1.920 sample/frame` trong đường publication đa rate.
- Audit tăng thẳng từ production `1.6` lên `1.8` để không đụng schema nghiên cứu `1.7`, ghi `sequenceTiming` gồm frame rate, NDF, sample rate, samples/frame và policy `phase12-integer-ndf-exact-frame-grid-v1`. PCM evidence tăng `1.2`; audit cũ thiếu timing chỉ được fallback về grid legacy 25 fps, còn schema `1.8+` thiếu/sai timing bị từ chối.
- Comparator yêu cầu noise-boundary provenance cho mọi schema từ `1.6` trở lên và timing hợp lệ cho `1.8+`; review/shadow overlap và gain-capped marker đều tái tính từ cùng grid.
- End-to-end synthetic package ở `24/25/30` chứng minh DOM và streaming XML tương đương, sequence/clip timebase được giữ, fragment/marker đúng, review ghi đúng fps và audit ghi lần lượt `2.000/1.920/1.600` sample/frame.
- Sau khi các validator đạt, inspector production nhận `24/30` khi sequence và từng clip ghi rõ `ntsc=FALSE`; thiếu metadata dừng trước media read, 25 fps thiếu `ntsc` vẫn tương thích.
- Toàn bộ Release đạt `198/198`; solution build 0 warning/error, format và diff check sạch. Đây mới là synthetic publication gate, chưa phải bằng chứng Premiere adoption.

### Slice 12D — regression 25 fps và pilot local

- Toàn bộ test Release, format, diff check, self-contained publish và installer smoke đạt.
- HGE2 và full HGE `25 fps` phải có coverage mismatch `0`, lost Enabled `0`, M19 Enabled, không đổi status/gain/marker/XML semantic so với Phase 10.
- Runtime full HGE dưới `90 phút`, peak RAM dưới `1,5 GB`, temporary file count `0`.

Kết quả 12D:

- HGE2 schema `1.8` giữ đúng `8.291` fragment; comparator với Phase 10 đạt, coverage mismatch `0`, lost Enabled `0`, newly Enabled `0`, temporary file `0`. M19/A3 frame `11214–11218` tiếp tục Enabled. XML SHA-256 `5A5127F9B4DCFEB7C38FAD50FA9A0BFFB58BF073D9DB4A8EBD93031B7052C2AD`; audit `1B287255F72B42BDF74DC1374426AC002F4CE7469BFCBEC3D85C85BF2ADAEA63`; runtime `76,7 giây`.
- Full HGE schema `1.8` giữ đúng `162.922` fragment; comparator đạt với source hash khớp, coverage mismatch `0`, lost Enabled `0`, newly Enabled `0`, temporary file `0`. Runtime `77 phút 02 giây`, peak working set `1.280,5 MB`, dưới hai cổng `90 phút / 1,5 GB`. XML SHA-256 `13573F508CA95E12EDB1D10B19322AE63B3D06ABFAF87AA24DCB32C2B98DE70D`; audit `5BF843CE0405C7BD33EE9601FB1AEBB5F6AF8A2AACAB56EC2831150ED026E981`.
- Self-contained publish và installer smoke đạt: `410` payload file, app mở được, install/uninstall exit `0`, payload được gỡ, file người dùng tạo vẫn còn, registry entry được tạo/xóa đúng. Installer unsigned nội bộ SHA-256 `8C479106D1019266BE0405307A37548B209E0BF2DF4B8F1ADD2ACA697BF29A85`; RC1 và release công khai không đổi.
- CI vẫn bắt buộc build/test/publish và installer smoke. Upload bản sao installer chỉ chạy ngoài pull request, giữ `7 ngày` và là best-effort để quota artifact của GitHub không biến một installer đã kiểm đạt thành lỗi chất lượng giả.

### Slice 12E — Premiere adoption 24/30 fps

- Mỗi rate có một fixture Premiere riêng với source trim, gap, clip nối tiếp, phrase qua biên WAV, Disable và gain đại diện.
- Import đúng candidate XML; xác nhận duration, timing, media relink, track/routing và marker.
- Export PCM mono `48 kHz` từ đầu sequence cho từng track cần đo. Phrase không cap phải đạt `-6,0 ±0,1 dBFS`; phrase cap khớp predicted peak và không nóng hơn target.
- Re-export Final Cut Pro XML và so semantic timing/source/Enabled/gain/marker với đúng candidate.
- Chỉ rate vượt toàn bộ cổng mới được thêm vào phạm vi sản phẩm. Một rate thất bại không kéo rate còn lại vào adoption.

Kết quả 12E:

- `scripts/phase12/New-IntegerNdfPremiereFixture.ps1` tạo fixture local/offline, không ghi đè, với speech mức vừa, speech rất nhỏ, source trim, phrase qua biên hai clip, gap một giây và trailing silence. Cùng WAV nguồn được dùng cho 24/30 để cô lập biến frame-grid.
- Inspector nhận cả hai fixture với `0 warning / 0 error`. App tạo mỗi candidate trong dưới `1,1 giây`: cùng `2 phrase` gồm `1 uncapped` qua hai source clip và `1 capped +18 dB`, `6 fragment`, `4 marker`, `2 review group`; Disabled xuất hiện ở silence.
- Candidate 24 fps có XML SHA-256 `69322FF3D8CD335C99A9505AEA84002F0C19D499CB95941179D61EB4851316D2`, audit `FBFBD945F3390D0F09CDE3EF1EE6CE7164E38814F1C34705EF9BB555370941CD`, grid `2.000 sample/frame`.
- Candidate 30 fps có XML SHA-256 `F5929453819147F8D201EC0F5B942CCA42E02867D4CA90E9712EB31A0045FEC0`, audit `D65F3045A7C740DE91D2010B049FEAF3DD16C1B692AA50053313F528274454F5`, grid `1.600 sample/frame`.
- Hai candidate được import vào Premiere Pro 2026 và tạo PCM mono `48 kHz / 24-bit` cùng Final Cut Pro XML re-export mới trong hai thư mục evidence riêng. PCM 24 fps SHA-256 `EE601B250E7D87B7016947EED25E8A37561F191F8D2B1C57FB73AE983A2C7D1A`; PCM 30 fps `8067C493C0260BB2CAD97C6CA06A27B2BF7020A67F5305D7E3F45A953BC2A97E`.
- PCM validator `1.2` đạt ở cả hai rate: `2/2 phrase`, `0` failure; phrase không cap đạt target hậu routing và phrase `+18 dB` cap khớp predicted peak trong tolerance. 24 fps dùng `2.000 sample/frame`; 30 fps dùng `1.600 sample/frame`.
- XML re-export giữ `6/6` clip, `4 Enabled`, `2 Disabled`, `4` marker và semantic gain với maximum delta `0,0000138 dB` ở cả hai rate. XML 24 fps SHA-256 `1ED321A0E3AAA6844DF63E87D761E8B384B58DE34FD11A479B709C065DBB0291`; XML 30 fps `757260934261096728CFCB89422FE62A8AD9010A57982B8DD52811F296CC3CB8`.
- Premiere 2026 chuẩn hóa duy nhất sequence metadata depth từ `24` thành `16` khi re-export, dù PCM thực vẫn là mono `24-bit`. Round-trip validator mặc định tiếp tục từ chối mismatch này; Phase 12 chỉ cho phép explicit normalization `sequence-depth-24-to-16`, ghi vào report schema `1.2`, và vẫn từ chối mọi depth transition khác. Clip/media/timing/Enabled/gain/marker không được miễn.
- Adoption report đạt status giới hạn `phase12-integer-ndf-premiere-artifacts-compatible`: 24 fps SHA-256 `AC46489421A7915277E30E3216ADBC728179E4DEF133ACD1D568B78926A7CB3F`; 30 fps `E0202CDC3E255E86CE442BB8FDEB09C685511CCDE846FF24FA5C960C9F156A7D`. Đường dẫn và checklist vận hành nằm ở [PHASE12_PREMIERE_OPERATOR_CHECKLIST.md](PHASE12_PREMIERE_OPERATOR_CHECKLIST.md).

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

Chạy Release/build/format/diff cuối trên đúng branch, cập nhật PR bằng toàn bộ evidence và merge Phase 12 sau khi CI xanh. Cổng nâng cấp tiếp theo phải được mở thành phase riêng; không gộp stereo source, resampling, fractional-rate hoặc routing profile mới vào Phase 12 đã khóa.
