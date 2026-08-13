# Phase 11 — Bleed đa mic có calibration

Trạng thái: `in progress; Slice 11A passed` ngày 2026-08-13. Phase được mở từ `main` commit `3f41d7cd2d8b4d90241b4bd2f50f798e23d65796`, sau khi Phase 10 đã merge và toàn bộ CI hậu merge đạt. Branch làm việc: `codex/phase11-calibrated-multimic-bleed`. Slice 11A chỉ thêm corpus/test baseline; production audio, status, gain, marker và XML chưa thay đổi.

## Quyết định sản phẩm

Phase 11 tiếp tục nâng chất lượng lõi xử lý âm thanh và XML. Không thêm preview, playback, màn hình review hoặc editor trong app. Luồng chính vẫn là chọn XML Premiere, kiểm tra media, phân tích offline và xuất một XML mới cùng bằng chứng kỹ thuật.

Đây không phải thay đổi cốt lõi MVP. XML, WAV và `.prproj` nguồn luôn bất biến; vùng lời được giữ và cân peak; chỉ noise/bleed có bằng chứng rõ mới được Disable; vùng mơ hồ luôn Enabled và có marker `Cần kiểm tra`.

## Khoản nợ kỹ thuật hiện tại

`BleedResolver` hiện quyết định một candidate từ một vùng chồng với phrase của mic khác:

- mỗi lần chỉ đọc một cửa sổ tối đa `1.000 ms`, lấy ở giữa overlap nếu vùng dài hơn;
- yêu cầu mic khác cao hơn mic đích ít nhất `12 dB`, correlation từ `0,80`, lag không quá `12 ms` và mic đích thấp hơn mức direct voice đã học;
- chọn comparison có correlation cao nhất;
- residual trên `-10 dB` được xem là xung đột direct/bleed và giữ Ambiguous, còn evidence vượt ngưỡng có thể thành Bleed.

`CrossTrackShadowEvidenceBuilder` dùng cùng phép đo một cửa sổ cho một nhóm ambiguity hẹp. Phase 08 đã chứng minh shadow evidence không đổi status, Enabled, gain, marker hoặc XML, nhưng HGE2/full HGE khi đó không có `LikelyBleed` hay `ConflictingEvidence`.

Các giới hạn cần xử lý:

- một cửa sổ thuận lợi có thể không đại diện cho quan hệ âm học ổn định giữa hai mic;
- ngưỡng delay/gain chung không mô tả được từng cặp mic và hướng truyền `mic nguồn → mic đích`;
- comparison hiện không chứng minh delay, suy hao, correlation và residual lặp lại trên nhiều đoạn độc lập;
- một candidate có thể tự cung cấp phần lớn evidence dùng để kết luận cho chính nó;
- giọng nói đồng thời, room tone chung, clipping, đảo cực, media gap hoặc clock drift có thể tạo evidence không ổn định.

Đây là rủi ro về độ tin cậy của quyết định bleed, không phải bằng chứng rằng một tỷ lệ cụ thể của output Phase 10 đã sai.

## Mục tiêu Phase 11

Xây dựng một candidate bleed đa mic theo hai tầng:

1. học fingerprint có hướng cho từng cặp track từ nhiều cửa sổ direct-speech độc lập, gồm delay, suy hao tương đối, correlation và residual cùng độ phân tán;
2. chỉ đề xuất Bleed khi nhiều cửa sổ của candidate khớp fingerprint ổn định và không có bằng chứng direct speech xung đột trên mic đích.

Nếu calibration thiếu support, không ổn định, phụ thuộc một vùng duy nhất hoặc các phép đo bất đồng, kết quả phải giữ Ambiguous/Enabled. Không được hạ ngưỡng chỉ để tăng số vùng Disabled.

## Giá trị mong đợi và giới hạn kết luận

Giá trị dài hạn là nhận diện thêm bleed rõ ràng giữa các mic mà không làm mất lời trên mic đích. Trước mắt, Phase 11 phải tạo được calibration và shadow evidence có provenance, deterministic và kiểm tra được.

Chủ dự án đã miễn tập nhãn nghe Mục tiêu ở các phase trước. Vì vậy, khi chưa có tập nhãn Target hợp lệ, Phase 11 không được tăng tự động mute trong XML production và không được tuyên bố phần trăm speech/noise/bleed accuracy. Candidate có thể hoàn tất ở trạng thái shadow-ready; production vẫn giữ semantic Phase 10.

Muốn chuyển thêm bất kỳ vùng Phase 10 Enabled nào thành Disabled, bắt buộc phải có một cổng adoption riêng với nhãn Target hợp lệ và bằng chứng Premiere mới từ đúng candidate XML. Note Context hoặc nghe toàn mix không thay thế cho cổng này.

## Không thuộc Phase 11

- Không đổi Silero VAD `6.2.1`, FIR chống alias, noise floor hoặc VAD hysteresis Phase 10.
- Không đổi target gain, routing, boost cap hoặc encoding XML Phase 00.
- Không thêm preview/review UI, playback, editor hoặc workflow nghe trong app.
- Không mở thêm frame rate, sample rate, stereo source hoặc routing profile; phần đó thuộc Phase 12.
- Không thêm fade/effect, LUFS, true peak, limiter hoặc Master-bus processing.
- Không tối ưu tốc độ bằng cách bỏ calibration window, shadow comparison, validator hoặc provenance.

## Hợp đồng phải giữ nguyên

- Mọi Speech và Ambiguous phải Enabled; chỉ Noise/Bleed có evidence hợp lệ mới Disabled.
- M19/A3 frame `11214–11218` phải vẫn Enabled.
- Target vẫn là sample peak từng phrase gần `-6 dBFS` sau `mono-center-equal-power-to-stereo`, bù `+3,0102999566 dB`, boost tối đa `+18 dB`.
- Encoding gain Phase 00, frame-safe reference, whole-phrase fallback và hai lớp merge bảo thủ Phase 10 không đổi.
- Input và output cũ không bị sửa/ghi đè. Mỗi pilot dùng thư mục mới; chỉ công bố XML/audit/CSV sau validator.
- Kết quả không phụ thuộc cách chia block PCM hoặc `MaximumWorkers` 1–4.
- Không ghi media path riêng tư hoặc waveform thô vào audit; chỉ ghi provenance, thống kê bounded và hash cần thiết.

## Baseline Phase 10 bị đóng băng

- Release đạt `153/153` test; app candidate CI `31689521606` và CI hậu merge `31691393143` đạt.
- HGE2 cuối: `78,356 giây`, peak `260,9 MB`, 2.136 phrase, 8.291 fragment, 5.207 marker; XML SHA-256 `6F855699ED6D39712F3118A661DCF18943750F805D3EDB662546D033A808910E`; audit SHA-256 `21A18FB7799D171AB9BCD744B65D1ECEBF3E9EA8DE9321B0C707476AAAD6E534`.
- So Phase 09 trên HGE2: coverage mismatch `0`, mất Enabled `0 interval / 0 frame`; M19 vẫn Ambiguous/Enabled.
- Premiere PCM A1–A7: 2.136/2.136 phrase khớp timing/gain; 921/921 phrase không cap ở target; M19 có đủ rendered audio và correlation nguồn gần 1.
- Premiere XML re-export: 8.291/8.291 clip, 4.488 Enabled, 3.803 Disabled và 5.207 marker giữ semantic; max gain delta `0,000055244 dB`.
- Full HGE: `74 phút 56,5 giây`, peak `1.320,4 MB`; coverage mismatch `0`, mất Enabled `0 interval / 0 frame`, tệp tạm `0`.

Baseline dùng để phát hiện regression, không phải target cần tăng số Disabled bằng mọi giá.

## Thiết kế candidate

### Fingerprint có hướng theo cặp mic

Với mỗi hướng `track nguồn → track đích`, candidate thu thập nhiều anchor window từ các phrase direct-speech chắc chắn của track nguồn. Anchor phải có media đầy đủ, đủ năng lượng và không có evidence direct-speech/xung đột chưa giải quyết trên track đích.

Fingerprint tối thiểu cần ghi:

- track nguồn, track đích và version policy;
- số anchor, tổng thời lượng và độ phân bố theo timeline;
- median/range hoặc robust spread của lag và attenuation;
- phân bố correlation và residual;
- lý do chấp nhận hoặc từ chối calibration;
- hash của ordered evidence stream để validator có thể đối chiếu mà không giữ toàn bộ waveform.

Fingerprint là có hướng vì suy hao từ A sang B không được giả định bằng từ B sang A. Không suy diễn fingerprint qua track thứ ba.

### Chống self-confirmation

Candidate đang được đánh giá không được là evidence duy nhất tạo fingerprint cho chính nó. Implementation phải dùng leave-one-region-out hoặc cơ chế tương đương; nếu bỏ candidate làm support tụt dưới mức tối thiểu thì kết quả là insufficient calibration, không phải Bleed.

### Quyết định nhiều cửa sổ

Một candidate dài phải được chia thành nhiều cửa sổ cố định. Chỉ khi advantage, correlation, lag và residual cùng phù hợp fingerprint trên đủ số cửa sổ độc lập mới được gọi là `CalibratedLikelyBleed` trong shadow.

Các trường hợp sau luôn fail-safe về Ambiguous/Enabled:

- lag hoặc attenuation trôi vượt tolerance của fingerprint;
- residual cho thấy còn direct component đáng kể trên mic đích;
- chỉ một cửa sổ vượt ngưỡng hoặc các cửa sổ bất đồng;
- thiếu media, cửa sổ quá ngắn, clipping/non-finite, gap hoặc clip boundary không đủ evidence;
- hai mic cùng có direct speech hoặc không xác định được hướng nguồn/đích;
- calibration thiếu support hoặc không ổn định.

Ngưỡng số anchor, thời lượng cửa sổ và tolerance chưa được khóa trong tài liệu mở phase. Chúng phải được chọn từ synthetic response và shadow distribution, rồi version hóa trước pilot adoption; không tuning theo một timecode riêng lẻ.

## Phạm vi triển khai

### Slice 11A — khóa corpus và baseline một cửa sổ

- Thêm fixture tổng hợp: delayed/attenuated copy ổn định, nhiều delay/gain theo cặp, independent speech, simultaneous speech, room tone chung, polarity inversion, clipping, short overlap, media gap, clip boundary và drift theo thời gian.
- Ghi rõ output của resolver Phase 10 trên corpus trước khi thêm candidate.
- Khóa ranh giới kiến trúc: calibrator ở 11B phải dùng API shadow riêng, không sửa trực tiếp production resolver hoặc XML trong 11A.

Kết quả triển khai 11A:

- `Phase11BleedSyntheticCorpus` tạo 11 kịch bản PCM deterministic, không chứa media riêng tư: copy trễ/suy hao ổn định, chọn đúng nguồn trong ba track, fingerprint trôi ngoài cửa sổ giữa, giọng độc lập, direct+bleed đồng thời, room tone chung, đảo cực, clipping, overlap dưới 120 ms, media gap chung và ranh giới hai clip liền nhau.
- Snapshot Phase 10 khóa 8 ca hiện thành Bleed; 2 ca giữ Ambiguous không có evidence; ca direct+bleed đồng thời giữ Ambiguous với `conflicting-direct-and-bleed-evidence`. Mọi evidence vượt ngưỡng hiện hành vẫn phải đúng source track, correlation `>=0,80`, advantage `>=12 dB`, lag dự kiến và residual đúng phía của cổng `-10 dB`.
- Ca dài 4 giây được lập trình delay/gain khác nhau ở ba vùng: ngoài trái `-8 ms / 0,06`, cửa sổ giữa `4 ms / 0,08`, ngoài phải `11 ms / 0,12`. Resolver Phase 10 vẫn kết luận Bleed với lag tuyệt đối `4 ms`, xác nhận nó chỉ dùng cửa sổ giữa tối đa một giây và chưa kiểm tính nhất quán toàn vùng.
- Targeted test đạt `2/2`; toàn bộ Release đạt `155/155`; `dotnet format --verify-no-changes` và `git diff --check` sạch.
- Không sửa file nào dưới `src/`, không đổi schema audit/XML và không tạo candidate output. Vì semantic production không thể thay đổi ở 11A, chưa chạy HGE2/full HGE/Premiere; các gate đó chỉ bắt buộc khi candidate được nối ở slice sau.

### Slice 11B — calibrator theo cặp track

- Xây dựng fingerprint có hướng, robust với outlier và có minimum support rõ ràng.
- Bảo đảm leave-one-region-out, deterministic ordering, cancellation và bounded memory.
- Xuất trace/audit bounded có policy version, summary, evidence hash và rejection reason.
- Không thay `TrackAudioAnalysis` production hoặc XML.

### Slice 11C — scorer nhiều cửa sổ và shadow comparison

- So candidate với cả resolver một cửa sổ hiện tại và final Phase 10 trên cùng PCM/observation.
- Phân loại shadow: `NoCalibration`, `BelowCalibratedThreshold`, `ConflictingEvidence`, `CalibratedLikelyBleed`.
- Audit dự kiến tăng `1.6` → `1.7`; validator kiểm track pair, policy, evidence hash, window counts, coverage và mọi trạng thái Enabled.
- App không thêm màn hình; evidence kỹ thuật nằm trong audit/CSV output hiện có.

### Slice 11D — cổng adoption có nhãn Target

- Lập tập Target cô lập, ghi đúng mic đích và đoạn cần giữ/tắt; Context-only không hợp lệ để tính metric.
- Khóa threshold trước khi chấm tập Target; không sửa threshold sau khi xem lỗi từng timecode mà không mở vòng nghiên cứu mới.
- Chỉ cho phép candidate ảnh hưởng production khi direct speech được giữ, ambiguous không bị Disable và clear bleed đạt cổng đã định trước.
- Nếu chưa có tập Target hợp lệ, dừng ở shadow-ready và production XML giữ semantic Phase 10.

### Slice 11E — pilot, Premiere round-trip và đóng gói

- Chạy Release tests, format, HGE2 và full HGE trong artifact directory mới; so với đúng Phase 10 baseline.
- Full HGE vẫn dưới `90 phút`, peak dưới `1,5 GB`, temporary file count `0`.
- Nếu production XML audio không đổi, chứng minh hash/semantic không đổi và không tái làm PCM vô ích.
- Nếu adoption làm XML đổi, bắt buộc dùng đúng hash candidate cho Premiere import → PCM A1–A7 → M19 → Final Cut Pro XML re-export; không tái gắn evidence Phase 10.
- Chạy self-contained publish, installer smoke test và CI trên HEAD cuối. Phase không tự động thay RC1.

## Cổng nghiệm thu

### Calibration tổng hợp

- Fingerprint ổn định được học từ nhiều anchor độc lập và từ chối pair thiếu support.
- Delay/gain/correlation/residual nhất quán mới tạo `CalibratedLikelyBleed`; drift hoặc disagreement luôn fail-safe.
- Không self-confirmation; bỏ candidate khỏi calibration có thể làm outcome lùi về `NoCalibration`.
- Kết quả invariant theo block/worker, deterministic giữa các lượt chạy và bounded memory.

### Safety và XML

- Trước cổng Target: Phase 10 Enabled → Phase 11 production Disabled bằng `0 interval / 0 frame` trên synthetic, HGE2 và full HGE.
- Mọi Speech/Ambiguous final Enabled; M19 vẫn Enabled.
- Coverage, gain, marker, routing và XML semantic không đổi nếu candidate vẫn shadow-only.
- Validator thất bại trước publication khi provenance, evidence hash, pair direction, support count hoặc Enabled contract không hợp lệ.

### Adoption nếu có nhãn Target

- Metric chỉ tính trên Target listening labels hợp lệ và báo cả denominator, false mute và vùng unresolved.
- Không chấp nhận tăng clear-bleed Disable nếu đổi lại bằng bất kỳ direct-speech loss chưa được giải quyết nào.
- Mọi vùng mới Disabled có calibrated multi-window evidence và truy ngược được về policy/fingerprint.
- Premiere PCM/XML round-trip đạt đúng giới hạn timing/gain/marker hiện hành.

## Điều kiện dừng hoặc rollback

- Nếu fingerprint chỉ hoạt động bằng cách dựa vào một anchor, một timecode hoặc threshold hạ theo pilot, giữ Phase 10.
- Nếu runtime/RAM vượt gate, tối ưu caching/streaming; không bỏ cửa sổ hoặc provenance để ép đạt.
- Nếu HGE2/full HGE cho calibration quá ít hoặc không ổn định, kết luận `insufficient evidence`; không tự suy ra Bleed.
- Nếu chưa có Target labels hợp lệ, không merge logic làm tăng mute production.
- Nếu Premiere không giữ timing/gain/Disable/marker sau XML thay đổi, rollback adoption dù synthetic đạt.

## Bước tiếp theo

Bắt đầu Slice 11B: xây calibrator có hướng theo cặp track, minimum support và leave-one-region-out trên corpus đã khóa. Calibrator tiếp tục chỉ tạo shadow evidence; chưa sửa production status, gain, marker hoặc XML.
