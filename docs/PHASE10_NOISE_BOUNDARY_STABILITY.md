# Phase 10 — Ổn định noise floor và ranh giới câu

Trạng thái: `Slice 10C implemented; candidate shadow-only` ngày 2026-08-11 trên branch `codex/phase10-noise-boundary-stability`. Phase này bắt đầu từ `main` commit `06ad4d6`; baseline logic audio/XML là Phase 09 merge commit `50ee4f9`. Slice 10A–10C thêm corpus, trace, comparator, estimator và VAD hysteresis candidate; đường production/XML output chưa thay đổi.

## Quyết định sản phẩm

App tiếp tục chỉ làm tốt ba việc: đọc XML Premiere và media, xử lý âm thanh offline, rồi xuất XML mới có thể kiểm tra. Phase 10 không thêm preview, playback, màn hình review, editor hoặc workflow quyết định trong app.

Phase 10 là nâng cấp chất lượng lõi, không thay đổi bản chất MVP. Input XML/WAV/`.prproj` vẫn bất biến; vùng mơ hồ vẫn Enabled; chỉ noise/bleed có bằng chứng rõ mới được Disable; output vẫn là XML mới cùng audit/CSV kỹ thuật.

## Khoản nợ kỹ thuật hiện tại

### Noise floor

`AdaptiveNoiseFloor` hiện dùng percentile 20 của cửa sổ 512 observation, tương đương khoảng `16,384 giây` ở block 32 ms, rồi làm mượt đối xứng với hệ số `0,15`.

`AudioFrameEvidenceBuilder` đưa mọi frame có media và VAD dưới `0,50` vào estimator trước khi so RMS của chính frame đó với `noiseFloor + 10 dB`. Hệ quả cần nghiên cứu:

- frame VAD-negative nhưng năng lượng cao có thể tham gia nâng floor trước khi được nhận diện là xung đột;
- frame đầu tiên của một vùng media tự đặt floor theo chính nó, nên warm-up chưa có hợp đồng rõ ràng;
- estimator không phân biệt tốc độ floor tăng và giảm khi room tone thay đổi;
- audit hiện không cho biết frame nào được dùng hoặc bị loại khỏi quá trình học floor.

Đây là rủi ro về độ ổn định của bằng chứng năng lượng, không phải bằng chứng rằng Phase 09 đã nhận diện sai một tỷ lệ cụ thể.

### Ranh giới câu

`TrackDialogueAnalyzer` hiện dùng một ngưỡng VAD `0,50`. Chỉ frame đạt ngưỡng tham gia `BuildVadGroups`; các frame được gom khi khoảng cách nhỏ hơn `350 ms`. Một group chỉ thành phrase khi cả tổng VAD evidence và direct-energy evidence đạt tối thiểu `120 ms`.

Frame có xác suất `0,35..0,50` và đủ năng lượng được giữ Ambiguous độc lập. Padding phrase vẫn là `200/300 ms`, chia tại midpoint để không chồng gain. Cách này an toàn nhưng biên core có thể nhạy với probability dao động quanh `0,50`, tạo đảo trạng thái hoặc fragment vụn ở đầu/cuối câu.

## Mục tiêu Phase 10

Tạo candidate noise/boundary pipeline có hai đặc tính:

1. noise floor chỉ học từ frame đủ điều kiện background, không để frame đang được đánh giá tự làm nhiễm ngưỡng của chính nó;
2. biên câu dùng start/continue hysteresis có context hữu hạn để giảm nhạy với dao động VAD, nhưng không kéo câu qua khoảng nghỉ dài hoặc tự động mute thêm audio.

Candidate phải chạy shadow trên cùng observation với baseline Phase 09. Chỉ được adopt qua merge bảo thủ: mọi frame Phase 09 Enabled vẫn Enabled; bất đồng chưa có bằng chứng độc lập trở thành Ambiguous, không thành Noise/Disable.

## Không thuộc Phase 10

- Không đổi model Silero VAD `6.2.1`, checksum hoặc FIR chống alias Phase 09.
- Không tuning bleed đa mic; phần đó thuộc Phase 11.
- Không thêm frame rate, stereo source, sample rate hoặc routing profile mới; phần đó thuộc Phase 12.
- Không thêm fade/effect để xử lý click tại biên XML.
- Không đổi UI chính, installer trust, release signing hoặc cơ chế update.
- Không tối ưu runtime bằng cách làm yếu shadow evidence, validator hoặc full-HGE gate.
- Không dùng tổng số phrase/fragment giảm để tự tuyên bố nhận diện tốt hơn.

## Hợp đồng phải giữ nguyên

- Preset Cân bằng giữ start VAD `0,50`, lời tối thiểu `120 ms`, phrase break tối đa `350 ms` và padding `200/300 ms` trong giai đoạn candidate.
- `DirectVoiceAboveNoiseDb` vẫn là `10 dB`; thay đổi nằm ở cách floor được học, không âm thầm hạ ngưỡng direct voice.
- Mọi Speech và Ambiguous phải Enabled; chỉ Noise/Bleed hợp lệ mới Disabled.
- M19/A3 frame `11214–11218` phải vẫn Enabled.
- Target vẫn là sample peak từng phrase gần `-6 dBFS` sau `mono-center-equal-power-to-stereo`, bù `+3,0102999566 dB`, boost tối đa `+18 dB`.
- Encoding gain Phase 00, frame-safe peak policy, whole-phrase fallback Phase 09 và validator trước publication không đổi.
- Không sửa hoặc ghi đè XML, WAV, `.prproj`, audit hay output cũ. Mỗi run tạo thư mục mới và chỉ công bố artifact sau validator.

## Baseline Phase 09 bị đóng băng

- Release: `122/122` test đạt; CI hậu merge logic `31461869682` đạt.
- HGE2 replacement: `84,200 giây`, `203,3 MB`, 2.183 phrase, 10.831 fragment, 5.219 marker; XML `A11D046019BDF0F8677E9E952FE45DE7489DF3C079F578CAABCDF3F38FE57AA3`; audit `D62901F88A3A5EDC380D548FE43B2440D7CAC1C77A24F739E525B43C91519C7A`.
- Premiere PCM A1–A7: 2.183/2.183 phrase khớp timing/gain; 940/940 phrase không cap ở target; M19 nghe được và vẫn Enabled.
- Premiere XML re-export: 10.831/10.831 clip, 7.025 Enabled, 3.806 Disabled, 5.219 marker giữ semantic; max gain delta `0,000055244 dB`.
- Full HGE: `38 phút 33,1 giây`, peak `1.065 MB`, 43.603 phrase, 247.515 fragment, 126.485 marker; XML `0E8D67B6935423635260CBC0F652E57D451CB6A8E537B58BD8CF9D8AAD658844`; audit `0BC66E5EC986E28E27E9588DF4E268667125CA7C6C30B61673E6E5390B0003C2`.
- So với Phase 08 trên full HGE: coverage mismatch `0`, legacy Enabled bị mất `0 interval / 0 frame`, tệp tạm `0`.

Baseline dùng để đối chiếu regression và provenance. Không lấy tổng số baseline làm target cần giảm bằng mọi giá.

## Phạm vi triển khai

### Slice 10A — khóa corpus và hợp đồng estimator

- Thêm fixture tổng hợp không chứa dữ liệu riêng tư: stationary room tone, floor step-up/step-down, silence, loud-first-frame, VAD-negative high-energy conflict, speech sát ngưỡng, probability jitter, media gap và clip boundary.
- Tách chế độ `Phase09Baseline` và `NoiseBoundaryCandidate` để hai pipeline nhận cùng observation, cùng PCM và cùng preset.
- Ghi baseline theo từng frame: floor trước update, điều kiện học, floor sau update, VAD state và boundary state.
- Chưa cho candidate thay XML ở slice này.

Kết quả triển khai 10A:

- Có hai mode tường minh `Phase09Baseline` và `NoiseBoundaryCandidate`; cả hai nhận cùng track, observation, PCM và preset. Candidate 10A cố ý chạy đúng logic Phase 09 để tạo mốc so sánh bằng `0` trước khi 10B đổi estimator.
- API `AnalyzeShadow` chạy riêng baseline/candidate và giữ per-frame trace gồm floor trước update, eligibility, floor sau update, VAD/direct-energy và boundary state. Luồng `Analyze` sản xuất không capture trace, không chạy candidate và không đưa dữ liệu Phase 10 vào XML/audit.
- Comparator báo frame/phrase/interval khác nhau và đếm riêng `baseline Enabled → candidate Disabled`; test đã chứng minh comparator phát hiện một regression Enabled giả lập.
- Corpus tổng hợp không chứa dữ liệu riêng tư bao phủ stationary room tone, step-up/step-down, silence, loud-first-frame, VAD-negative high-energy conflict, speech sát ngưỡng, probability jitter, media gap và clip boundary.
- Test baseline khóa rõ hai khoản nợ hiện tại để 10B sửa có chủ đích: loud-first-frame tự học chính nó; high-energy VAD-negative conflict vẫn được coi là training-eligible trong Phase 09.
- Release đạt `130/130` test; build không warning/error; `dotnet format --verify-no-changes` sạch. Chưa chạy HGE2/full HGE/Premiere vì 10A không tạo XML candidate.

### Slice 10B — estimator background-eligible

- Quyết định direct/high-energy của frame hiện tại phải dùng floor từ state trước frame; chỉ sau đó mới xem frame có đủ điều kiện học hay không.
- Frame VAD speech, frame không media, frame high-energy conflict và frame warm-up chưa đủ bằng chứng không được nâng floor như background chắc chắn.
- Warm-up phải deterministic và fail-safe: khi chưa có floor đáng tin, vùng năng lượng đáng kể được giữ Ambiguous thay vì tự coi là Noise.
- Floor luôn finite và nằm trong `[AudioMath.SilenceDbfs, 0]`; không NaN/Infinity hoặc phụ thuộc cách chia block.
- Candidate dùng attack/release bất đối xứng cho floor tăng/giảm. Tham số chỉ được khóa sau test step-response; audit phải ghi rõ giá trị và version policy.

Kết quả triển khai 10B:

- Policy candidate được khóa là `phase10-background-eligible-p20-v1`: cửa sổ `512`, percentile `20`, initial floor `-90 dBFS`, warm-up `8 frame = 256 ms`, floor-rise smoothing `0,05`, floor-fall smoothing `0,20`.
- Frame hiện tại luôn dùng `floorBefore` để tính direct/high-energy. Chỉ sau quyết định đó estimator mới xét update; VAD speech, no-media, level không finite và high-energy conflict đều không được học như background.
- Warm-up chỉ thu thập candidate; chưa thay trusted floor trước frame thứ 8. Vùng warm-up vượt `floorBefore + 10 dB` trở thành `WarmupAmbiguous`, Enabled với reason `ambiguous-noise-floor-warmup`, nên loud-first-frame không tự học chính nó rồi bị coi là Noise.
- Để estimator vẫn theo kịp room-tone tăng thật, một step VAD-negative chỉ được promote sau `16 frame = 512 ms` liên tục có spread tối đa `3 dB`. Trước mốc này floor đứng yên; sau mốc dùng rise `0,05`. Floor giảm dùng release `0,20` khi percentile window chuyển xuống.
- Floor được clamp trong `[-144, 0] dBFS`; NaN/Infinity bị loại và không đổi state. Trace ghi policy version/tham số, readiness trước/sau, eligibility và floor trước/sau; comparator từ chối trace sai policy provenance.
- Synthetic step-response xác nhận rise/fall đơn điệu, không overshoot; repeated-run deterministic; conflict 4 frame sau warm-up không nâng floor; outlier loud-first không làm lệch floor bootstrap.
- Candidate vẫn chỉ chạy qua `AnalyzeShadow`. App production vẫn gọi `Analyze` → `Phase09Baseline`, không capture trace và không đưa candidate vào status/gain/marker/audit/XML. Policy sẽ được ghi vào audit `1.6` khi shadow được tích hợp ở 10D.
- Release đạt `137/137` test; build không warning/error; `dotnet format --verify-no-changes` sạch. Chưa chạy HGE2/full HGE/Premiere vì 10B chưa tạo XML candidate.

### Slice 10C — VAD start/continue hysteresis

- Start threshold giữ `0,50`.
- Continue threshold chỉ được nghiên cứu trong khoảng `[0,35; 0,50)` và chỉ có hiệu lực sau một start hợp lệ; giá trị cuối phải xuất phát từ synthetic response + shadow report, không chọn theo một timecode riêng lẻ.
- Continue/context không được nối qua khoảng nghỉ từ `350 ms` trở lên và không thay minimum direct evidence `120 ms`.
- Borderline context thiếu direct evidence vẫn là Ambiguous Enabled, không được nâng thành Speech chỉ để giảm fragment count.
- Padding `200/300 ms`, midpoint split và frame-safe gain reference giữ nguyên.

Kết quả triển khai 10C:

- Policy `phase10-vad-start050-continue040-v1` khóa start `0,50`, continue `0,40`, phrase break `350 ms`, minimum start evidence `120 ms` và minimum direct evidence `120 ms`. Candidate từ chối preset shadow nếu start bị đổi khỏi `0,50` khi chưa có nghiên cứu mới.
- State machine chỉ mở group bằng frame đạt start. Sau đó frame có media và probability `>=0,40` mới được dùng làm continue context; probability dưới `0,40` không được nhận. Gap từ accepted frame cuối `>=350 ms` hoặc no-media đóng group ngay.
- Continue frame không được cộng vào start/direct evidence. Vì vậy một start 96 ms cộng nhiều context vẫn không thành phrase; chuỗi `0,40..0,49` không có start cũng không tạo phrase.
- Continue context đã có start hợp lệ nhưng thiếu direct-energy được giữ `Ambiguous`, Enabled, cùng phrase/gain với reason `ambiguous-vad-continue-context-near-speech`; không được nâng thành Speech để làm đẹp fragment count.
- Boundary trace phân biệt confirmed/unconfirmed start, confirmed/unconfirmed continue và continue-ambiguous. Trace ghi đầy đủ VAD boundary policy; comparator từ chối policy provenance sai.
- Synthetic response khóa các trường hợp: `.50` start, `.40` continue, `.39` reject; jitter quanh threshold; không hạ minimum evidence; không bridge đúng mốc 350 ms; media gap đóng context; baseline Enabled → candidate Disabled bằng `0` trên corpus.
- Release đạt `146/146` test; build không warning/error; `dotnet format --verify-no-changes` sạch. Candidate vẫn chỉ chạy qua `AnalyzeShadow`; chưa có audit `1.6`, XML candidate, HGE/Premiere pilot hoặc thay đổi app production.

### Slice 10D — shadow comparison và merge bảo thủ

- Với mỗi resampling front-end, chạy baseline/candidate trên cùng observation để tách chênh lệch noise/boundary khỏi chênh lệch FIR Phase 09.
- Tạo comparison theo track và từng interval: floor delta, training eligibility, phrase start/end, split/merge, status/reason, Enabled và gain.
- Audit dự kiến tăng `1.5` → `1.6`, thêm policy/provenance và toàn bộ noise-boundary differences cần thiết để chạy lại gate.
- Final merge giữ mọi baseline Enabled. Nếu candidate muốn Disable vùng baseline giữ lại, final phải giữ Ambiguous/Enabled và ghi disagreement.
- Nếu bất kỳ phần nào của phrase baseline cần fail-safe, whole-phrase coverage/gain Phase 09 vẫn được giữ trọn.
- `OutputDecisionContractValidator` phải thất bại trước publication nếu coverage, Enabled/status, phrase/gain/target/cap hoặc marker không nhất quán.

### Slice 10E — pilot, round-trip và đóng gói

- Chạy synthetic corpus, HGE2 và full HGE trên artifact directory mới.
- Dùng comparator đối chiếu candidate với đúng Phase 09 baseline; source hash, output hash và tệp tạm phải được xác minh.
- Nếu XML audio thay đổi, bắt buộc Premiere import → PCM A1–A7 → M19 → Final Cut Pro XML re-export bằng đúng candidate hash. Không tái gắn PCM Phase 09.
- Chạy Release tests, format, self-contained publish, installer smoke test và GitHub Actions trên HEAD cuối.

## Cổng nghiệm thu

### Estimator và boundary tổng hợp

- Kết quả deterministic qua mọi cách chia block PCM và `MaximumWorkers` 1–4.
- Frame high-energy conflict không được dùng để nâng floor của chính nó hoặc các frame kế tiếp như background chắc chắn.
- Stationary floor hội tụ ổn định; step-up/step-down đi đơn điệu, không overshoot và tuân đúng attack/release đã ghi trong audit.
- Loud-first-frame/warm-up không bị tự động xem là background rồi Disable do thiếu lịch sử.
- Hysteresis không tạo phrase nếu chưa có start hợp lệ; không bridge gap `>=350 ms`; không hạ minimum direct evidence `120 ms`.
- Mọi fixture giữ coverage liên tục, không overlap/mất sample/frame và không thay đổi theo clip boundary/media gap.

### Safety regression

- Phase 09 baseline Enabled → final Disabled: `0 interval / 0 frame` trên synthetic, HGE2 và full HGE.
- Mọi Speech/Ambiguous final Enabled; mọi disagreement có reason/marker và xuất hiện trong comparison.
- M19 vẫn Enabled.
- Gain/XML validator đạt; phrase không cap vẫn target `-6,0 ±0,1 dBFS` trong Premiere PCM nếu XML thay đổi.
- Không thay model, routing, gain cap, XML encoding hoặc input immutability.

### Pilot và hiệu năng

- HGE2 và full HGE hoàn tất không error ngoài warning metadata đã biết.
- Full HGE dưới `90 phút`, peak toàn tiến trình dưới `1,5 GB`, temporary file count `0`.
- Report phải liệt kê phrase/fragment/marker, short-island, split/merge và mọi changed interval. Các số này là bằng chứng cấu trúc, không phải phần trăm nhận diện.
- Không tuyên bố speech/noise/bleed accuracy nếu chưa có Target listening labels hợp lệ.

### Packaging

- Toàn bộ Release test cũ + test Phase 10 đạt; `dotnet format --verify-no-changes` sạch.
- Publish `win-x64` self-contained và installer smoke test đạt; không thêm Python, Internet, telemetry hoặc model download.
- PR CI xanh trên HEAD cuối; chỉ merge sau khi mọi pilot/round-trip bắt buộc đạt.

## Điều kiện dừng hoặc rollback

- Nếu candidate làm mất bất kỳ frame Phase 09 Enabled nào, không adopt candidate và không hạ gate.
- Nếu estimator/hysteresis chỉ làm đẹp tổng số fragment nhưng changed intervals không giải thích được, giữ Phase 09 baseline.
- Nếu runtime/RAM vượt gate, tối ưu implementation; không bỏ shadow comparison hoặc provenance để ép đạt.
- Nếu Premiere round-trip không giữ timing/gain/Disable/marker, Phase 10 không được merge dù synthetic và HGE đều xanh.

## Bước tiếp theo

Thực hiện Slice 10D: tích hợp shadow comparison ở cấp project/front-end, thêm audit `1.6` và conservative merge giữ mọi baseline Enabled; mở rộng validator để chặn coverage/status/phrase/gain/marker sai trước publication. Chưa chạy Premiere pilot hoặc adopt candidate nếu các gate 10D chưa đạt.
