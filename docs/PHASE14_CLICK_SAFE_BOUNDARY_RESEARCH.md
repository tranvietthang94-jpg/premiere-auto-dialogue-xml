# Phase 14 — Nghiên cứu chất lượng biên cắt âm thanh

Trạng thái: `in progress; Slice 14E đang chờ xác nhận nghe`. Phase mở ngày 2026-08-24 từ clean `main` commit `90e8e2d81a2a20903a349f50bec10b953a8794c5` trên branch `codex/phase14-click-safe-boundary-research`.

## Quyết định sản phẩm

Phase 14 chỉ nghiên cứu nguy cơ click/pop do app chia source clip thành các fragment Enabled/Disabled hoặc đổi gain tại biên frame. App tiếp tục chỉ xử lý âm thanh offline và xuất XML mới; không thêm preview, playback, editor, effect hoặc automation vào production khi chưa có bằng chứng Premiere tương xứng.

Phép đo discontinuity không phải kết luận nghe. Một bước biên lớn chỉ là ứng viên cần kiểm tra; không được dùng để tự động nới vùng Enabled, thêm fade hoặc đổi quyết định speech/noise/bleed.

## Hợp đồng khóa

- Không sửa XML, WAV hoặc `.prproj` nguồn; output/report luôn dùng đường dẫn mới.
- Speech và Ambiguous luôn Enabled; M19/A3 frame `11214–11218` tiếp tục Enabled.
- Không đổi VAD, noise floor, bleed, phrase padding, gain target hậu routing `-6 dBFS`, compensation `+3,0102999566 dB`, boost cap `+18 dB` hoặc encoding Audio Levels Phase 00.
- Chỉ phân tích biên do app tạo bên trong cùng source clip. Biên giữa hai source clip gốc không được quy lỗi cho app.
- Installer và bằng chứng thật chỉ giữ cục bộ; GitHub chỉ lưu source code và CI không upload artifact.

## Baseline

- Phase 13 `main` tại `90e8e2d`; worktree sạch.
- Release `201/201` test, policy Premiere 24/30 `8/8`, publish `410` payload và installer smoke đạt.
- HGE2/full HGE giữ coverage mismatch/lost Enabled/newly Enabled bằng `0`; M19 Enabled.
- XML production hiện dùng hard Enabled/Disabled theo fragment frame-aligned. Fragment Enabled mang Audio Levels; fragment Disabled không mang gain filter.
- CI hậu merge Phase 13 có một lượt test đồng thời timeout ở `AnalyzeAsyncNeverExceedsFourWorkers`; rerun cùng commit đạt. Đây là test scheduling flake, không phải bằng chứng lỗi analyzer.

## Mục tiêu

1. Làm phép thử giới hạn bốn worker ổn định trên runner chậm mà vẫn chứng minh analyzer có concurrency và không vượt bốn worker.
2. Tạo boundary scanner đọc đúng PCM nguồn và audit đã khóa hash, đo bước sample tại các chuyển trạng thái/gain do app tạo.
3. Báo riêng `Enabled → Disabled`, `Disabled → Enabled` và đổi gain; giữ count/hash đầy đủ cùng top sample bounded.
4. Chạy synthetic corpus có biên gần zero crossing và biên amplitude cao để chứng minh metric phân biệt được hai trường hợp.
5. Quét HGE2 trước; chỉ quét full HGE nếu runtime/RAM bounded và kết quả HGE2 có ích.
6. Chỉ mở candidate XML transition nếu có ứng viên amplitude cao lặp lại và bằng chứng nghe/render thực tế cho thấy click do app tạo.

## Slice 14A — CI test determinism

- Test tự chuẩn bị tối thiểu bốn ThreadPool worker rồi khôi phục cấu hình trong `finally`.
- Không tăng timeout để che deadlock và không thay `AudioProjectAnalyzer` production.
- Chạy test mục tiêu lặp lại nhiều lần trước full suite và CI.

## Slice 14B — boundary discontinuity scanner

- Input: audit output, XML nguồn khớp SHA-256 và WAV nguồn đã qua inspector.
- Chỉ xét hai fragment liền nhau, cùng track, cùng `SourceClipId`, timeline chạm nhau và state/gain render thay đổi.
- Đọc sample cuối phía trái và sample đầu phía phải từ đúng source range; áp gain tuyến tính `10^(dB/20)` cho phía Enabled, dùng zero cho phía Disabled.
- Ghi source step, rendered step và phần excess do quyết định render. Giá trị dBFS dùng floor hữu hạn cho zero.
- Threshold chỉ là `screening threshold`; report không dùng từ `audible`, `click confirmed` hoặc tỷ lệ chất lượng chưa đo.
- Report mới không overwrite, ghi SHA-256 audit/XML và không chứa đường dẫn tuyệt đối.

## Slice 14C — synthetic corpus

- Biên Enabled/Disabled gần zero crossing phải cho rendered step thấp.
- Biên Enabled/Disabled tại amplitude cao phải được xếp trước trong candidate list.
- Biên giữa hai source clip gốc bị loại khỏi app-created count.
- Fragment liên tục cùng state/gain không tạo candidate.
- Gain change được báo riêng và không bị đánh đồng với mute transition.

## Slice 14D — corpus thật

- Chạy trên artifact directory mới, bắt đầu bằng HGE2 Phase 13.
- Ghi count theo transition kind, maximum/p95 rendered step, số candidate qua threshold, full-stream hash và top sample bounded.
- Đối chiếu source XML hash, fragment count và M19 trước khi dùng report.
- Không commit XML/WAV/audit/report thật.

## Slice 14E — cổng Premiere tùy điều kiện

Chỉ mở nếu Slice 14D cho bằng chứng đáng điều tra:

1. xác định một số boundary cụ thể trên duplicate sequence;
2. nghe và render PCM quanh boundary hiện hành;
3. nếu click được xác nhận, tạo candidate nhỏ bằng cấu trúc XML do Premiere xuất thật;
4. import/re-export và so timing/source/Enabled/gain/marker cùng rendered PCM;
5. không adopt nếu Premiere thêm normalization ngoài policy hoặc candidate làm mất bất kỳ Enabled frame nào.

Nếu không có click được xác nhận, Phase 14 đóng `research-only; no production change`.

## Kết quả thực thi 2026-08-24

### Scanner PCM nguồn

- `BoundaryDiscontinuityScanner` và CLI `Inspect --boundary-report` khóa SHA-256 audit/XML, coverage/source mapping và chỉ quét biên do app tạo trong cùng `SourceClipId`.
- Synthetic suite phân biệt biên amplitude cao với gần zero, báo riêng mute/enable/gain, bỏ biên giữa hai source clip và deterministic trên empty stream.
- HGE2 Phase 13 có `8.291` fragment, `8.284` biên do app tạo, `8.176` transition (`3.803` Enabled → Disabled, `3.798` Disabled → Enabled, `575` gain change). Screen nguồn `-40 dBFS` giữ `1.816` candidate; maximum excess `-3,1743 dBFS`, p95 `-29,9364 dBFS`, transition hash `0E6BE9F62FF73FD5CF354400F9390C2159A67CB77896AE94319A5F55AEB13B63`.
- Report cục bộ: `private-artifacts/phase14-boundary-hge2-20260824-1/phase14-hge2-boundary-report.json`, SHA-256 `6757FB647EE17D0B3EE335A0142669AFF4B67514366069B345F0B22328217437`. Report chỉ là screening, không kết luận nghe.

### Đối chiếu PCM Premiere thật

- Audit Phase 10 gắn với bảy PCM Premiere và audit Phase 13 đều có source XML SHA-256 `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`, cùng `8.291` fragment. So sánh từng fragment theo track/source clip/file, timeline, source sample, status, Enabled và applied gain có `0` mismatch; comparator cũng có coverage mismatch/lost Enabled/newly Enabled bằng `0`.
- `PremierePcmBoundaryScanner` đọc trực tiếp full-sequence mono PCM 48 kHz, đo sample trước/sau boundary và so bước đó với p99 derivative trong cửa sổ hai phía `±10 ms`. Candidate cần đồng thời step `>= -40 dBFS` và cao hơn local p99 ít nhất `12 dB`; đây vẫn là transient screen, không phải nhãn audible.
- Bảy track có đủ `8.176` transition và `982` transient screening candidate: `410` Enabled → Disabled, `414` Disabled → Enabled, `158` gain change.
- Điểm lớn nhất nằm tại A2 `00:07:25:15` (frame `11140`), Noise Disabled → Ambiguous Enabled với gain `+17,487829 dB`: PCM nhảy `0 → 0,490762`, tương đương `-6,1826 dBFS`, cao hơn local p99 `40,7494 dB`. Đây là bằng chứng rendered PCM thật để mở Slice 14E, nhưng chưa tự động chứng minh người nghe nhận ra click.
- M19/A3 frame `11214–11218` vẫn Enabled và không bị báo như transition trong vùng này.
- Bảy report cục bộ: `private-artifacts/phase14-premiere-pcm-boundary-20260824-1/phase14-pcm-A1-report.json` đến `phase14-pcm-A7-report.json`.

### Gói nghe cần operator xác nhận

Ba excerpt PCM mono 48 kHz/24-bit, dài đúng `2 giây`, đặt boundary tại giây thứ `1`:

1. `A2-00-07-25-15-disabled-to-enabled.wav`, SHA-256 `0758F2E2CACBA1279BC4A911A825E098143CBCBA266001A59F93FFBDC3D437F1`.
2. `A7-00-07-14-03-gain-change.wav`, SHA-256 `FCA44F180098779D49027D8D40FF9FED5484A0A7434C5003DF18E883FB3EBA5F`.
3. `A2-00-19-46-17-enabled-to-disabled.wav`, SHA-256 `8FEBA8179099AA1015C385BC4AD18F96AB00F898B9AB6E24D18C7E5BF541B51E`.

Thư mục cục bộ: `private-artifacts/phase14-listening-clips-20260824-1`. Chỉ khi operator xác nhận nghe click/pop tại chính giữa excerpt mới nghiên cứu candidate XML nhỏ. Trước xác nhận này production writer giữ nguyên.

## Cổng nghiệm thu

- Test flake được sửa không đổi code production và đạt lặp lại/CI.
- Scanner fail closed khi hash, media, coverage hoặc source sample range không khớp.
- Synthetic metric có kết quả deterministic và không tuyên bố audibility.
- Report bounded, không chứa absolute path và không ghi đè input/output.
- Production XML/audio semantic giữ nguyên trừ khi Slice 14E đạt đầy đủ bằng chứng Premiere.

## Điều kiện rollback

- Nếu scanner cần đoán source mapping, gain hoặc render state, dừng và giữ boundary đó `not-scannable`.
- Nếu phép đo tăng RAM/runtime không bounded trên HGE2, tối ưu scanner hoặc đóng research; không sửa production để phục vụ tool.
- Nếu candidate làm đổi Enabled, timing, gain ngoài transition đã chứng minh hoặc M19 regression, rollback toàn bộ candidate.
