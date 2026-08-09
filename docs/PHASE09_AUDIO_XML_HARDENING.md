# Phase 09 — Làm chắc lõi xử lý âm thanh và XML

Trạng thái: `kickoff` ngày 2026-08-09.

## Quyết định sản phẩm

App tiếp tục là một công cụ chuyên dụng: chọn XML Premiere, kiểm tra media, xử lý âm thanh và xuất XML mới. Không xây playback, preview, màn hình review hay workflow quyết định trong app.

Audit và CSV Phase 08 vẫn được giữ như bằng chứng kỹ thuật có thể kiểm tra độc lập, nhưng không trở thành một nhánh sản phẩm riêng. UI chính tiếp tục ưu tiên luồng bốn bước đơn giản hiện có.

## Mục tiêu Phase 09

Nâng độ tin cậy của đầu vào VAD và lớp bảo vệ trước khi một vùng âm thanh bị Disable, đồng thời giữ nguyên hợp đồng gain/XML đã được Premiere round-trip xác nhận.

Phase 09 bắt đầu từ một khoản nợ DSP cụ thể: `TrackAudioScanner` hiện đưa PCM 48 kHz về 16 kHz cho Silero bằng cách lấy mỗi mẫu thứ ba. Cách decimation trực tiếp này không có bộ lọc chống alias, nên năng lượng trên 8 kHz có thể gập xuống dải VAD và làm bằng chứng speech/noise kém ổn định. Việc sửa phải được thực hiện theo chế độ so sánh bảo thủ; không được âm thầm đổi hàng loạt XML chỉ vì thay front-end.

## Phạm vi triển khai

### Slice 09A — khóa baseline và hợp đồng resampler

- Đóng băng baseline Phase 08 cho synthetic fixtures, HGE2 và full HGE: số phrase/fragment/marker, trạng thái, Enabled, gain, runtime và peak RAM.
- Thêm corpus DSP tổng hợp không chứa dữ liệu riêng tư: DC, impulse, tone trong passband, tone trên Nyquist 16 kHz, sweep, silence, biên block và gap giữa clip.
- Chốt resampler streaming 48 kHz → 16 kHz, deterministic và giữ trạng thái đúng qua mọi block đọc PCM.
- Cổng số ban đầu:
  - cùng waveform phải cho cùng output dù bị chia block khác nhau;
  - DC/unity và dải lời chính không được lệch mức ngoài tolerance đã test;
  - tín hiệu trên 8 kHz phải được suy giảm đủ trước decimation thay vì alias vào dải 0–8 kHz;
  - mỗi `32 ms` timeline tạo đúng `512` mẫu VAD, không trôi sample qua clip/gap.
- Tolerance FIR cụ thể chỉ được khóa sau khi test đáp ứng tần số của implementation; không chọn số đẹp rồi hạ cổng để hợp thức hóa code.

### Slice 09B — resampler chống alias và shadow comparison

- Thay phép lấy mỗi mẫu thứ ba bằng polyphase FIR hoặc streaming FIR tương đương, không nạp toàn bộ WAV vào RAM và không thêm phụ thuộc cloud/runtime ngoài gói self-contained.
- Reset trạng thái VAD/resampler tại discontinuity dài theo đúng policy hiện có; gap ngắn vẫn giữ timeline liên tục bằng silence như trước.
- Trong giai đoạn pilot, chạy legacy và candidate trên cùng input để xuất difference report ngoài XML: vùng đổi speech/noise/ambiguous, phrase boundary, gain và marker.
- Candidate không được chuyển một vùng legacy Enabled thành Disabled khi hai pipeline bất đồng; trường hợp đó phải giữ Enabled/ambiguous cho tới khi có bằng chứng độc lập.
- Vùng legacy Disabled nhưng candidate phát hiện speech/ambiguous được giữ Enabled. Chính sách bất đối xứng này ưu tiên không làm mất lời.

### Slice 09C — validator quyết định trước XML

- Thêm kiểm tra độc lập giữa kết quả analysis và generation plan trước khi writer chạy:
  - mọi speech và ambiguous phải Enabled;
  - chỉ noise/bleed có bằng chứng hợp lệ mới được Disabled;
  - phrase/frame coverage liên tục, không trùng hoặc mất range;
  - gain của mọi fragment trong phrase khớp phrase và không vượt `+18 dB`;
  - predicted post-routing peak được tính bằng đúng policy frame-safe đã khóa;
  - marker bảo vệ `Cần kiểm tra` và `gain-capped` không bị mất.
- Validator thất bại phải dừng trước khi công bố XML/audit/CSV; input và các run cũ vẫn bất biến.
- Không dùng validator này để tuyên bố nhận diện đúng theo nội dung nghe; nó chỉ chứng minh hợp đồng nội bộ không tự mâu thuẫn.

### Slice 09D — pilot và cổng chấp nhận

- Synthetic DSP: đạt toàn bộ phép thử resampler, block continuity, reset/gap và cancellation.
- HGE2: M19/A3 tại `00:07:28:15` vẫn Enabled; toàn bộ thay đổi so với baseline được liệt kê, không che bằng tổng số thống kê.
- Full HGE: hoàn tất dưới `90 phút`, peak toàn tiến trình dưới `1,5 GB`; input hash không đổi và không có artifact tạm sau run.
- Nếu candidate làm XML audio thay đổi, bắt buộc tạo audit mới và chạy Premiere import → PCM → re-export tương ứng. PCM cũ không được gắn sang hash XML/audit mới.
- Nếu chưa có tập nhãn Target hợp lệ, báo cáo chỉ được kết luận fidelity DSP, hợp đồng XML/gain và safety regression; không tuyên bố phần trăm nhận diện speech/noise/bleed.

## Hợp đồng không được thay đổi

- Silero VAD vẫn là `6.2.1` với checksum đã ghim; Phase 09 sửa front-end PCM, không đổi model hoặc tải model sau cài.
- Preset Cân bằng giữ VAD `0.50`, speech tối thiểu `120 ms`, phrase break `350 ms`, padding `200/300 ms` cho tới khi có phase tuning riêng.
- Ambiguous luôn Enabled và có marker `Cần kiểm tra`; bằng chứng xung đột không được tự động mute.
- Target vẫn là sample peak từng phrase gần `-6 dBFS` sau routing `mono-center-equal-power-to-stereo`, compensation `+3,0102999566 dB`, boost tối đa `+18 dB`.
- Encoding XML gain Phase 00 không đổi: tới `+12 dB` dùng Audio Levels; trên `+12` tới `+18 dB` dùng Audio Levels `+12 dB` cộng Premiere Gain-filter remainder dạng tuyến tính.
- Không sửa/ghi đè XML, WAV hoặc `.prproj` nguồn. Mỗi run tạo thư mục mới và chỉ công bố artifact sau khi kiểm tra xong.

## Cổng nghiệm thu Phase 09

- Toàn bộ `105/105` test Release hiện có tiếp tục đạt; test mới bao phủ resampler và validator.
- Kết quả không phụ thuộc kích thước block PCM hoặc số worker.
- Không có legacy Enabled → candidate Disabled khi pipeline bất đồng.
- M19 vẫn được giữ; mọi ambiguous vẫn Enabled.
- HGE2/full HGE đạt cổng runtime/RAM hiện hành.
- Khi XML thay đổi, Premiere round-trip mới đạt timing/gain theo đúng giới hạn bằng chứng; không suy rộng thành LUFS, true peak, limiter hoặc Master-bus guarantee.
- Publish self-contained và installer Windows tiếp tục đạt, không thêm Python, Internet, telemetry hoặc model download.

## Các nâng cấp tiếp theo được đề xuất

### Ưu tiên 1 — Phase 10: noise floor và ranh giới câu ổn định hơn

- Không để mọi frame VAD-negative tự động tham gia học noise floor; loại high-energy conflict khỏi mẫu nền.
- Dùng estimator có attack/release rõ ràng và test cho room tone thay đổi theo thời gian.
- Thử hysteresis cho VAD start/continue và context ở biên câu, nhưng mọi threshold mới phải có shadow report và không được làm mất M19.
- Giá trị: giảm false negative/fragment vụn ngay trong lõi xử lý. Rủi ro: trung bình, cần dữ liệu pilot và so sánh từng vùng thay đổi.

### Ưu tiên 2 — Phase 11: bleed đa mic có calibration

- Học delay/gain fingerprint giữa các mic từ nhiều đoạn direct speech chắc chắn thay vì quyết định từ một cửa sổ tối đa một giây.
- Chỉ gọi `bleed` khi advantage, correlation, lag và residual cùng nhất quán trên nhiều cửa sổ; bất đồng vẫn là ambiguous.
- Giá trị: Disable được thêm bleed rõ ràng mà vẫn giữ lời mic đích. Rủi ro: cao, cần bằng chứng nghe Target hợp lệ trước khi tăng tự động mute.

### Ưu tiên 3 — Phase 12: mở rộng XML theo từng cổng Premiere

- Bắt đầu với sequence `24` và `30` fps NDF; mỗi rate có fixture, sample↔frame math, XML import, PCM và re-export riêng.
- Sau đó mới xem xét `23,976/29,97` và DF/NDF; không gộp tất cả frame rate vào một thay đổi.
- Stereo source hoặc sample rate khác 48 kHz là cổng riêng, yêu cầu channel mapping tường minh; không tự downmix hay đoán routing.
- Giá trị: nhận được nhiều dự án Premiere hơn. Rủi ro: cao ở timing/routing, phải giữ fail-safe từ chối profile chưa chứng minh.

### Ưu tiên 4 — nghiên cứu click-safe boundary

- Đo xem hard Enable/Disable tại biên frame có tạo click nghe được trên media thật hay không.
- Chỉ nếu có bằng chứng, thử fade rất ngắn bằng cấu trúc XML Premiere đã round-trip; không tự thêm effect/automation khi chưa chứng minh importer giữ đúng.
- Đây là research candidate, chưa phải cam kết implementation.

### Ưu tiên 5 — tối ưu tốc độ sau khi semantic đã khóa

- Cache các lần đọc PCM/correlation trùng range và giảm allocation trong full HGE.
- Mọi tối ưu phải cho 0 khác biệt semantic trên fixture cố định trước khi nhận kết quả nhanh hơn.
- Phase 08 đã đạt `25 phút 6 giây` và `785,6 MB`, nên tốc độ xếp sau độ chính xác âm thanh.

## Bước triển khai đầu tiên

Thực hiện Slice 09A: viết test mô tả chính xác hành vi decimation hiện tại, chốt hợp đồng resampler streaming và tạo shadow comparator. Chưa đổi XML product cho tới khi difference report cho HGE2/full HGE và cổng bảo vệ legacy Enabled đã hoạt động.
