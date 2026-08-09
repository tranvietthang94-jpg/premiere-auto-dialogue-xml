# Phase 09 — Làm chắc lõi xử lý âm thanh và XML

Trạng thái: `implementation-complete; premiere-round-trip-pending` ngày 2026-08-09. Candidate đã đạt toàn bộ gate tự động nhưng chưa được merge/adopt làm output chính thức vì XML audio đã thay đổi và chưa có PCM/re-export mới từ Premiere.

## Quyết định sản phẩm

App tiếp tục là một công cụ chuyên dụng: chọn XML Premiere, kiểm tra media, xử lý âm thanh và xuất XML mới. Không xây playback, preview, màn hình review hay workflow quyết định trong app.

Audit và CSV Phase 08 vẫn được giữ như bằng chứng kỹ thuật có thể kiểm tra độc lập, nhưng không trở thành một nhánh sản phẩm riêng. UI chính tiếp tục ưu tiên luồng bốn bước đơn giản hiện có.

## Mục tiêu Phase 09

Nâng độ tin cậy của đầu vào VAD và lớp bảo vệ trước khi một vùng âm thanh bị Disable, đồng thời giữ nguyên hợp đồng gain/XML đã được Premiere round-trip xác nhận.

Phase 09 bắt đầu từ một khoản nợ DSP cụ thể: baseline `TrackAudioScanner` đưa PCM 48 kHz về 16 kHz cho Silero bằng cách lấy mỗi mẫu thứ ba. Cách decimation trực tiếp này không có bộ lọc chống alias, nên năng lượng trên 8 kHz có thể gập xuống dải VAD và làm bằng chứng speech/noise kém ổn định. Candidate đã thay front-end theo chế độ so sánh bảo thủ; không âm thầm cho phép front-end mới làm mất vùng legacy Enabled.

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

- Toàn bộ `120/120` test Release đạt; test mới bao phủ resampler, shadow merge, validator và frame-safe phrase supersession.
- Kết quả không phụ thuộc kích thước block PCM hoặc số worker.
- Không có legacy Enabled → candidate Disabled khi pipeline bất đồng.
- M19 vẫn được giữ; mọi ambiguous vẫn Enabled.
- HGE2/full HGE đạt cổng runtime/RAM hiện hành.
- Khi XML thay đổi, Premiere round-trip mới đạt timing/gain theo đúng giới hạn bằng chứng; không suy rộng thành LUFS, true peak, limiter hoặc Master-bus guarantee.
- Publish self-contained và installer Windows tiếp tục đạt, không thêm Python, Internet, telemetry hoặc model download.

## Kết quả triển khai và bằng chứng tự động

### DSP, shadow gate và validator

- `StreamingFirDecimator3` thực hiện FIR streaming 127 tap, cửa sổ Blackman, cutoff 7 kHz và decimation 3. Output không phụ thuộc biên block; passband lệch dưới `0,05 dB`; tone 12 kHz bị suy giảm hơn `60 dB`; reset xóa đúng state; mỗi 32 ms tạo đúng 512 mẫu VAD.
- Mỗi track được chạy qua cả `LegacyStride3` và `AntiAliasFir`. Khi legacy Enabled nhưng candidate Disabled, kết quả cuối được giữ Enabled dưới trạng thái `ambiguous-vad-front-end-disagreement`; candidate chỉ được phép mở thêm vùng mà legacy từng Disable.
- Audit schema tăng từ `1.4` lên `1.5` và ghi toàn bộ chênh lệch front-end. `OutputDecisionContractValidator` dừng trước khi công bố artifact nếu vi phạm Enabled/status, coverage, gain/target/cap hoặc marker bảo vệ.
- Validator cho phép phrase ID bị thay thế do căn frame chỉ khi toàn bộ lõi phrase vẫn được phủ bởi fragment speech/ambiguous Enabled; chỉ một frame Disabled cũng làm run thất bại.

### Pilot HGE2

- Run private: `phase09-hge2-pilot-20260809-2`; runtime `82,105 giây`, peak working set `202,1 MB`, 0 error, 7 warning metadata dự kiến.
- Output có 2.389 phrase, 10.813 fragment, 5.302 marker và 2.354 review group. XML SHA-256 `DE06C3DF1BC188219E06BBF57FAF2341ABFA20EBF09089D4213B05913598FAF8`; audit SHA-256 `16C1545475C042C53CDDB6BADCF1097DB2B83A5850F026121B3E64FD3405FEE3`.
- So với baseline Phase 08 trên cùng source SHA-256 `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`: coverage mismatch `0`, legacy Enabled bị mất `0 interval / 0 frame`, giữ thêm 2.431 interval / 8.333 frame, tệp tạm `0`.
- M19/A3 frame `11214–11218` vẫn Enabled; một phần được bảo vệ bằng `ambiguous-vad-front-end-disagreement`, phần còn lại là `ambiguous-independent`.
- Front-end comparison: 454.471 observation; 448.901 probability thay đổi; 7.600 segment difference; 454 trường hợp legacy Enabled/candidate Disabled đều được fail-safe giữ lại; 3.151 trường hợp legacy Disabled/candidate Enabled được mở thêm.

### Pilot full HGE

- Run private: `phase09-full-hge-pilot-20260809-1`; hoàn tất trong `2.348,623 giây` (`39 phút 8,6 giây`), peak working set `1.024,2 MB`, 0 error, 27 warning metadata dự kiến. Cả runtime và RAM đều dưới gate `90 phút / 1,5 GB`.
- Output có 48.028 phrase, 246.363 fragment, 128.298 marker và 50.901 review group. XML SHA-256 `44609409B647C0AEB619DD7B05B6F287CAD2F3EA313E05C81F3C5BAF7D9CA687`; audit SHA-256 `B8D82AAC0705B3D9B3FB277F82706754B29FB3BAC3E6CB1281EAE22525BD9612`.
- So với baseline Phase 08 trên cùng source SHA-256 `4497FBBA2E6D5B834AA73929D6A3C6BADF9392BC1F0168E583411A9515ABCE90`: coverage mismatch `0`, legacy Enabled bị mất `0 interval / 0 frame`, giữ thêm 55.281 interval / 189.571 frame, tệp tạm `0`.
- Front-end comparison: 9.503.287 observation; 9.442.728 probability thay đổi; 204.507 segment difference; 12.327 trường hợp legacy Enabled/candidate Disabled đều được fail-safe giữ lại; 72.168 trường hợp legacy Disabled/candidate Enabled được mở thêm.
- Việc mở thêm nhiều vùng chứng minh policy bảo thủ hoạt động, không chứng minh độ chính xác nhận diện đã tăng. Không có Target listening labels hợp lệ nên không báo phần trăm speech/noise/bleed.

### Đóng gói

- Release test đạt `120/120` test.
- Publish `win-x64` self-contained giữ đúng 410 payload file. Installer unsigned thử nghiệm được tạo bằng Inno Setup 7.0.2, SHA-256 `225AD65E68D6806AC8426E4C39A8907CDD527E431E2154A7C0BA8A18A3C35CC8`.
- Smoke test installer đạt: cài, xác minh `410/410` file, mở app, gỡ payload, giữ file người dùng tạo và xóa đúng HKCU uninstall entry. Installer này chỉ là bằng chứng kỹ thuật private, không thay RC1 và không được phát hành.
- GitHub Actions PR run `31299251441` đạt trong `3 phút 1 giây`: restore, build, `120/120` test, publish self-contained, xác minh Inno Setup 7, build/cài/gỡ installer và upload artifact đều thành công.

### Công cụ đối chiếu lặp lại được

`tools/PremiereAutoDialogueXml.CompareAudits` đọc hai audit theo luồng, xác minh source/output hash, timeline coverage, status/Enabled, tệp tạm và thống kê mọi transition ở các biên frame hợp nhất. Gate thất bại nếu candidate làm mất bất kỳ frame legacy Enabled nào.

## Cổng còn lại trước khi merge/adopt

Candidate thay đổi XML audio đáng kể, nên bằng chứng PCM Phase 06/08 không được tái sử dụng. Cần import XML HGE2 Phase 09 vào một project Premiere thử riêng, export đủ A1–A7 thành mono PCM 48 kHz, chạy `PremiereAutoDialogueXml.VerifyPcm` cho từng track, xác nhận M19 nghe được và re-export XML. Chỉ sau khi gain/timing/Disable/marker đạt theo hash audit `1.5` mới đổi trạng thái Phase 09 thành `passed`, chuyển PR khỏi draft và cân nhắc merge.

Khả năng automation Windows trong phiên triển khai không cung cấp API hướng dẫn/xác nhận bắt buộc của skill, nên không thao tác Premiere mù. Đây là gate bằng chứng đang chờ, không phải lỗi code hay lý do hạ tiêu chuẩn nghiệm thu.

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

## Bước tiếp theo

Hoàn thành Premiere round-trip HGE2 cho đúng XML/audit Phase 09 nói trên. Không bắt đầu Phase 10 và không merge candidate trước khi gate này được đóng hoặc chủ dự án đưa ra quyết định waiver tường minh.
