# Phase 15 — rollout click-safe nhiều boundary

Ngày mở: 2026-08-24 (Asia/Saigon)  
Trạng thái: **hoàn tất ở phạm vi candidate/tooling — multi-boundary giảm click đạt, production adoption không đạt gain gate**

## Mục tiêu

Mở rộng bằng chứng Constant Gain 0 dB một frame của Phase 14 từ một boundary A2 sang một candidate nhiều boundary có giới hạn, có policy xung đột rõ ràng và bao phủ cả ba loại:

- `Enabled → Disabled`;
- `Disabled → Enabled`;
- thay đổi gain giữa hai fragment Enabled.

Phase này tiếp tục chỉ cải thiện chất lượng xử lý âm thanh và XML. Không thêm preview/playback/editor, không đổi VAD/noise/bleed/gain/routing/marker, không ghi đè input và không upload installer lên GitHub.

## Phạm vi an toàn

- Candidate tối đa `64` transition; pilot HGE2 hiện khóa ở `12`.
- Chỉ chọn transient candidate đã qua scanner Phase 14: rendered step `>= -40 dBFS` và cao hơn local p99 derivative ít nhất `12 dB`.
- Ưu tiên có đại diện của cả ba transition kind, sau đó xếp theo độ lệch local và rendered step.
- Hai boundary trên cùng track phải cách nhau tối thiểu `2` frame. Boundary thấp ưu tiên hơn bị ghi `skippedConflict`; writer cũng kiểm lại và từ chối toàn bộ batch nếu nhận request xung đột.
- Mọi boundary phải khớp generated XML, audit `1.9`, source clip/media, state, tick, source sample, frame grid `24/25/30 NDF` và audio `48 kHz`.
- Generated XML có transition sẵn, hash stale, kind sai, source handle thiếu hoặc output tồn tại đều fail closed; không để XML dở.
- Writer chỉ chèn `Cross Fade ( 0dB)` / `KGAudioTransCrossFade0dB`, `alignment=center`, một frame; không pre-normalize clip range/tick.

## Implementation

- `PremiereConstantGainBatchPlanner` tạo plan deterministic, risk-diverse và ghi full count/hash cùng quyết định bounded.
- `PremiereConstantGainBatchCandidateWriter` kiểm lại toàn bộ request trước khi ghi candidate mới theo temp-file + atomic move.
- CLI mới:

```text
PremiereAutoDialogueXml.Inspect --constant-gain-batch-candidate <audit.json> <source.xml> <generated.xml> <max-transitions> <new-output.xml> <new-report.json>
```

- Single-boundary writer Phase 14 dùng chung core validation mới; regression v3 vẫn được giữ.
- Synthetic corpus có ba boundary cách nhau hai frame, đủ mute/enable/gain; xác minh ba transition được thêm mà mọi clip giữ nguyên. Corpus xung đột một frame, kind mismatch và stale provenance đều phải dừng trước publication.

## Local gate

- Release build: `0` warning, `0` error.
- Full suite: `218/218` đạt.
- Format verify: `0` file thay đổi.
- Candidate HGE2:
  - XML `phase15-hge2-constant-gain-12.xml`, SHA-256 `915CE3F7BCA96D57F0845994CCE449CF2BDA743A6336E7C7D34FCA9AAF355802`;
  - report `phase15-hge2-constant-gain-12.report.json`, SHA-256 `926A12902E574A60431B89E92209A47C4209825442DDAD03F68A9140D67455EA`;
  - scanner thấy `1.282` transient candidate; capture bounded có `785`, báo rõ `497` không nằm trong selection universe của pilot;
  - chọn `12`: `3` Enabled→Disabled, `5` Disabled→Enabled, `4` gain change;
  - selection stream SHA-256 `10532947A9CFE4FC2D3B8B8C2C5AE2BCF393E503ED4B18E1BDEA45FC23D1F4AF`.
- So candidate với generated XML Phase 13: `8.291/8.291` clip, `4.488` Enabled, `3.803` Disabled, `5.207` marker, max gain delta `0`, mismatch `0`; report SHA-256 `957DA028588B7DB2F7316F820CF2635D178200BD5D41DE4C684BFFCFB3102AEA`.
- M19/A3 giữ nguyên nhờ candidate-vs-baseline mismatch `0`; production writer/app mặc định vẫn chưa adopt transition.

## Danh sách pilot Premiere

| Track | Frame | Timecode 25 fps | Kind |
|---:|---:|---:|---|
| A2 | 11140 | 00:07:25:15 | Disabled→Enabled |
| A2 | 29667 | 00:19:46:17 | Enabled→Disabled |
| A2 | 34512 | 00:23:00:12 | Disabled→Enabled |
| A2 | 36512 | 00:24:20:12 | Disabled→Enabled |
| A3 | 46313 | 00:30:52:13 | Enabled→Disabled |
| A6 | 12287 | 00:08:11:12 | Gain change |
| A6 | 43088 | 00:28:43:13 | Gain change |
| A7 | 10853 | 00:07:14:03 | Gain change |
| A7 | 23138 | 00:15:25:13 | Gain change |
| A7 | 30526 | 00:20:21:01 | Disabled→Enabled |
| A7 | 39116 | 00:26:04:16 | Enabled→Disabled |
| A7 | 39766 | 00:26:30:16 | Disabled→Enabled |

## Kết quả Premiere round-trip

- Người vận hành import đúng candidate, re-export XML và xuất full-sequence WAV mono PCM 48 kHz/24-bit cho A2, A3, A6 và A7.
- XML re-export `phase15-hge2-constant-gain-12-renlai.xml` có SHA-256 `D55443442BB6CD3603E0C55390429C8D44F5591ECC46F02C13A23046CF4A5C98`.
- Premiere giữ đúng `12` Constant Gain. Validator chấp nhận đúng `48` normalization đã khóa: với mỗi transition, Premiere đổi `end/start` của hai clip sang `-1` và mở `pproTicksOut/pproTicksIn` mỗi phía nửa frame theo source tick. Không có normalization tích lũy hoặc mismatch ngoài policy.
- Sau round-trip vẫn có `8.291/8.291` clip, `4.488` Enabled, `3.803` Disabled và `5.207` marker; max gain delta `0,0000552434 dB`. Report `phase15-premiere-roundtrip.report.json` có SHA-256 `E6B77A472338F9B44D6BF51CDB8834A95436FDE2F66D8758047D3178A525457E`.
- CI có regression policy tổng hợp ba transition: chấp nhận đúng 12 normalization của ba boundary và từ chối mutation Enabled. Validator lấy `pproTicksIn/Out` theo source clip, không suy diễn source tick từ timeline frame.

## Kết quả PCM và quyết định adoption

- Cả `12/12` boundary đều giảm rendered step so với baseline, trong khoảng `31,6284–61,9658 dB`, và không boundary nào còn vượt transient threshold Phase 14.
- M19/A3 frame `11214–11218` vẫn Enabled trong XML; cửa sổ PCM có `7.680` sample, peak `-35,207469 dBFS`, RMS `-48,234571 dBFS` và không im lặng.
- Full phrase gate đạt A3 `386/386`, A6 `274/274`, A7 `351/351`. A2 chỉ đạt `296/297`: phrase `T02-P000010-legacy-safety` tại frame `11140–11161`, gain `+17,487829 dB`, dự đoán `-6,0000003 dBFS` nhưng render còn `-8,7990888 dBFS`, lệch `-2,7990885 dB`.
- Regression A2 là hệ quả thực của transition smoothing: peak ngắn dùng để lập gain đã bị giảm. Vì vậy Phase 15 **không nối batch policy vào production writer** và app mặc định tiếp tục semantic Phase 13, không tự chèn transition.
- Candidate mechanics, XML round-trip validator và policy regression vẫn được giữ vì fail-closed và chỉ chạy khi gọi CLI candidate rõ ràng. Consolidated report `phase15-pcm-adoption.report.json` có SHA-256 `0F2DE05F0672C44674652936F456B2CFFCB789B12FF88A224A36BDC21E8DF0CB`, status `phase15-click-reduction-passed-production-gain-gate-failed`.

Phase 15 đóng mà không cần export thêm. Nếu tiếp tục hướng này, phase sau phải lập gain có xét envelope transition hoặc loại bảo thủ các phrase mà peak nằm tại boundary, rồi tạo một candidate và Premiere render mới; không được rollout hàng loạt từ bằng chứng hiện tại.
