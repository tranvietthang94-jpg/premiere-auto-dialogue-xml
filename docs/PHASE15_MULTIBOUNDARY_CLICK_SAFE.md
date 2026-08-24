# Phase 15 — rollout click-safe nhiều boundary

Ngày mở: 2026-08-24 (Asia/Saigon)  
Trạng thái: **đang thực hiện — implementation và local HGE2 gate đạt; chờ Premiere round-trip/PCM**

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

## Cổng Premiere còn lại

1. Import candidate HGE2, không chỉnh clip/transition thủ công.
2. Re-export Final Cut Pro XML mới.
3. Export full-sequence WAV mono PCM 48 kHz/24-bit từ đầu sequence cho A2, A3, A6 và A7; mỗi lần Solo đúng một track.
4. Validator phải thấy đúng `12` Constant Gain và chỉ các normalization một-lần đã khóa ở Phase 14; clip/Enabled/gain/marker ngoài policy không đổi, M19 vẫn Enabled.
5. Cả `12` focused boundary phải giảm bước PCM, không boundary nào tệ hơn baseline và không còn vượt cùng transient threshold.

Chỉ khi năm cổng này đạt mới cân nhắc nối policy batch vào production writer. Nếu một boundary tăng click, Premiere tạo normalization tích lũy hoặc mất Enabled/M19, Phase 15 rollback candidate và app tiếp tục semantic Phase 13.
