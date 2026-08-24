# Phase 16 — adoption transition bảo thủ theo gain gate

Ngày mở: 2026-08-24 (Asia/Saigon)  
Trạng thái: **đang thực hiện — production writer/app mặc định chưa chèn transition**

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

## Cổng local

1. Synthetic phải tái hiện phrase có peak nằm trong frame transition và loại boundary đó.
2. Boundary có peak tương đương ngoài vùng ảnh hưởng phải được giữ.
3. Thiếu phrase/gain/source, source peak không khớp audit, phrase conflict hoặc provenance stale đều phải dừng/loại trước khi tạo XML.
4. Candidate HGE2 mới phải loại A2/frame `11140`, giữ nguyên mọi clip/Enabled/gain/marker so với generated XML Phase 13 và không ghi đè artifact Phase 15.
5. Release build, full tests, Phase 13 policy, Phase 15 normalization policy và format verify đều đạt.

## Cổng Premiere trước adoption

1. Import đúng HGE2 candidate Phase 16 và re-export Final Cut Pro XML.
2. Export full-sequence WAV mono PCM 48 kHz/24-bit cho đúng các track có transition.
3. XML chỉ có normalization đã khóa, không semantic drift và M19 vẫn Enabled.
4. Mọi boundary được chọn phải giảm click; mọi phrase trên track render phải qua gain/target gate, không còn regression như A2/frame `11140`.
5. Chỉ sau khi bốn cổng trên đạt mới cân nhắc nối policy vào production writer. Nếu không đạt, app tiếp tục semantic Phase 13 không transition.

