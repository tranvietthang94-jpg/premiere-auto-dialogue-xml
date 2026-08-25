# Phase 16 — adoption transition bảo thủ theo gain gate

Ngày mở: 2026-08-24 (Asia/Saigon)

Ngày đạt: 2026-08-25 (Asia/Saigon)

Trạng thái: **đạt — production app tự chèn tối đa 12 transition đã qua gain-safety fail-closed**

## Mục tiêu

Giữ lợi ích giảm click của Constant Gain 0 dB một frame nhưng loại trước publication mọi boundary có thể làm thay đổi peak mà app dùng để lập gain. Phase này chỉ tác động candidate tooling và validator cho tới khi có đủ Premiere XML/PCM mới.

Không đổi VAD, noise/bleed, ambiguity, marker, routing, target `-6 dBFS`, boost cap, gain encoding, input support, UI hoặc RC1. Không sửa input và không upload installer lên GitHub.

## Policy bảo thủ v1

- Chỉ xét transient candidate đã qua scanner Phase 14 và provenance Phase 15.
- Vùng ảnh hưởng được tính rộng một frame nguồn: frame cuối của phía Enabled trước boundary và frame đầu của phía Enabled sau boundary. Gain change kiểm cả hai phía.
- Mỗi phía Enabled phải có `PhraseId`, measured peak, applied gain và source mapping hợp lệ. Thiếu bằng chứng thì loại fail-closed.
- Đọc source PCM thật của toàn phrase và tính peak còn lại sau khi bỏ vùng ảnh hưởng. Peak còn lại phải giữ expected post-routing peak trong sai số `0,1 dB`; không tự tăng gain để bù.
- Một candidate không được chọn nhiều boundary cùng tác động một phrase. Xung đột phrase bị loại dù các boundary cách nhau trên timeline.
- Kết quả phải deterministic, ghi full count/hash và lý do cho cả boundary đạt lẫn bị loại.
- Writer vẫn kiểm lại audit/XML/hash/kind/source handle và chỉ chèn transition; không pre-normalize clip range/tick.

## Implementation

- `PremiereTransitionGainSafetyGate` đối chiếu scan/audit/source XML, đọc source PCM thật và ghi evidence/hash deterministic cho từng boundary.
- `PremiereConstantGainSafeBatchPlanner` chỉ xếp hạng boundary `Eligible`, vẫn giữ conflict khoảng cách Phase 15 và thêm conflict một transition cho mỗi phrase.
- CLI candidate riêng, không thay lệnh Phase 15 hoặc production app:

```text
PremiereAutoDialogueXml.Inspect --constant-gain-safe-batch-candidate <audit.json> <source.xml> <generated.xml> <max-transitions> <new-output.xml> <new-report.json>
```

- Synthetic khóa peak chỉ trong guard frame, peak tương đương ngoài guard frame, thiếu phrase provenance, source peak lệch audit và phrase conflict.

## HGE2 local candidate

- Dùng đúng source XML/audit/generated XML Phase 13 mà Phase 15 đã kiểm hash.
- Scanner có `1.282` transient candidate; `785` nằm trong captured universe, `497` không capture.
- Gain safety: `633` Eligible, `152` bị loại gồm `150` thiếu phrase provenance và `2` expected peak loss; source-peak mismatch `0`, no-retained-peak `0`.
- A2/frame `11140`, phrase `T02-P000010-legacy-safety`, bị loại: full source peak lệch audit chỉ `-0,00000064 dB`, nhưng bỏ guard frame làm retained expected peak từ `-6,0000003` xuống `-16,1472191 dBFS`, hụt `10,1472188 dB`.
- Boundary expected-peak-loss còn lại là A3/frame `27268`, phrase `T03-P000167`, hụt `3,5084822 dB`; cũng bị loại.
- Candidate chọn `12`: `5` Enabled→Disabled, `6` Disabled→Enabled, `1` gain change; selection SHA-256 `FB92805104949870B015F319F16FD0BF55CEA812ABF5CF5876C475137143F874`.
- XML `phase16-hge2-constant-gain-safe.xml`, SHA-256 `2F3FBD0DEEEFE3AA51A30A30141631A39435DE8378F38650DD646DA203DBAB54`.
- Report `phase16-hge2-constant-gain-safe.report.json`, SHA-256 `E07150C6AE82C2E632171CBDD17F041D1DF7E496978C5AB9DDF48D58E6B3C7DB`; decision stream SHA-256 `AEC4A6DF6D8B3D7F97633ED344143CBED67EB79A2586FCEB8008D869BCC255E8`.
- Comparator với generated XML Phase 13 có mismatch `0`: `8.291/8.291` clip, `4.488` Enabled, `3.803` Disabled, `5.207` marker và max gain delta `0`; report SHA-256 `349EB6B1237F7D9950D084BCADFBD11F7378B770E1D638A7EF554125657A02AF`.
- Release build sạch; full suite `223/223`, Phase 13 policy `8/8`, Phase 15 normalization policy và format verify đều đạt.

### Danh sách Premiere pilot

| Track | Frame | Timecode 25 fps | Kind |
|---:|---:|---:|---|
| A2 | 29667 | 00:19:46:17 | Enabled→Disabled |
| A2 | 34512 | 00:23:00:12 | Disabled→Enabled |
| A2 | 36512 | 00:24:20:12 | Disabled→Enabled |
| A2 | 44070 | 00:29:22:20 | Enabled→Disabled |
| A3 | 46313 | 00:30:52:13 | Enabled→Disabled |
| A5 | 35344 | 00:23:33:19 | Disabled→Enabled |
| A5 | 36606 | 00:24:24:06 | Enabled→Disabled |
| A5 | 49331 | 00:32:53:06 | Disabled→Enabled |
| A6 | 31926 | 00:21:17:01 | Gain change |
| A7 | 30526 | 00:20:21:01 | Disabled→Enabled |
| A7 | 39116 | 00:26:04:16 | Enabled→Disabled |
| A7 | 39766 | 00:26:30:16 | Disabled→Enabled |

## Cổng local

1. Synthetic phải tái hiện phrase có peak nằm trong frame transition và loại boundary đó.
2. Boundary có peak tương đương ngoài vùng ảnh hưởng phải được giữ.
3. Thiếu phrase/gain/source, source peak không khớp audit, phrase conflict hoặc provenance stale đều phải dừng/loại trước khi tạo XML.
4. Candidate HGE2 mới phải loại A2/frame `11140`, giữ nguyên mọi clip/Enabled/gain/marker so với generated XML Phase 13 và không ghi đè artifact Phase 15.
5. Release build, full tests, Phase 13 policy, Phase 15 normalization policy và format verify đều đạt.

Các cổng local đã đạt.

## Cổng Premiere trước adoption

1. Import đúng HGE2 candidate Phase 16 và re-export Final Cut Pro XML.
2. Export full-sequence WAV mono PCM 48 kHz/24-bit cho đúng các track có transition.
3. XML chỉ có normalization đã khóa, không semantic drift và M19 vẫn Enabled.
4. Mọi boundary được chọn phải giảm click; mọi phrase trên track render phải qua gain/target gate, không còn regression như A2/frame `11140`.
5. Chỉ sau khi bốn cổng trên đạt mới cân nhắc nối policy vào production writer. Nếu không đạt, app tiếp tục semantic Phase 13 không transition.

## Bằng chứng Premiere mới

- Premiere Pro re-export đúng candidate thành `phase16-hge2-constant-gain-safe_render lai.xml`, `17.165.085` byte, SHA-256 `BD7E2377ED637486B61587A48D08C48407341FEF5A6B71FFB186E52CBD68ED1F`.
- Round-trip giữ đúng `12` transition và chỉ có `48` clip normalization đã khóa; `8.291/8.291` clip, `4.488` Enabled, `3.803` Disabled, `5.207` marker; max gain delta `0,0000553 dB`. Report SHA-256 `55AC06A433D49946E4E247FE662E17E316B459EB8C87BD48C534E6D0B37B5EE5`.
- Năm WAV A2/A3/A5/A6/A7 đều mono PCM `48 kHz/24-bit`, dài `2.150,4 giây`; phrase gate đạt `1.668/1.668`, failed `0`.
- Cả `12/12` boundary rời transient-screening candidate. Rendered step giảm `26,58–48,94 dB`, trung vị `37,88 dB`; không còn regression A2/frame `11140` vì boundary đó không được chọn.
- M19 A3 frame `11214–11218` vẫn có PCM, peak `-35,207469 dBFS`, RMS `-49,203671 dBFS`, trùng baseline.

## Production adoption

- `ProductionTransitionPackageAdopter` chạy tự động sau output baseline và trước khi app trả kết quả. Người dùng không có thêm preview, setting hay bước thủ công.
- Scanner capture tối đa `1.024` sample; gain gate, phrase conflict và planner chọn tối đa `12`. Thiếu/stale evidence hoặc expected peak loss đều bị loại trước khi sửa XML.
- Candidate writer chỉ chèn Constant Gain 0 dB một frame; audit production tăng lên schema `2.0`, ghi policy/count/hash và danh sách boundary được chọn. XML/audit được commit theo cặp trong run mới; lỗi/cancel xóa run dở, input không bị sửa.
- HGE2 chạy qua đúng production `--write` trong `126,9 giây`, peak working set `284,1 MB`, tạo `12` transition. XML SHA-256 `84CD04782CEB33FBA71026FC3148284D9C5734EA9ED8066891391A473659642F` khớp hash trong audit; selection SHA-256 vẫn đúng `FB92805104949870B015F319F16FD0BF55CEA812ABF5CF5876C475137143F874`, safety decision SHA-256 `AEC4A6DF6D8B3D7F97633ED344143CBED67EB79A2586FCEB8008D869BCC255E8`; temp/backup còn lại `0`.
- Comparator production với candidate đã qua Premiere có mismatch `0`, giữ nguyên mọi clip/Enabled/Disabled/gain/marker và đủ 12 transition; report SHA-256 `C34D361E6ED582B2A6314D6A863A6F0F2FE99F697FC4E21DF2ADE9E24CF5021E`.
- Release build không warning, `223/223` test, Phase 13 policy `8/8`, Phase 15 normalization policy và self-contained publish `411` payload file đều đạt.
- PR `#22` CI run `32802975587` đạt toàn bộ build/test/policy/publish/installer smoke trong `3 phút 48 giây`; không upload artifact.

## Kết luận

Phase 16 đạt mục tiêu nâng chất lượng click mà không đổi VAD/noise/bleed/ambiguity, gain target, routing, marker, input contract, UI flow hoặc RC1. Production app đã adopt policy bảo thủ; mọi boundary không có đủ bằng chứng vẫn giữ semantic Phase 13, không transition.
