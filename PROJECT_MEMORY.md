# Memory handoff — Premiere Auto Dialogue XML, v0.1.0 Stable

Ngày cập nhật: 2026-08-26 (Asia/Saigon). Đây là memory source-of-truth để một phiên Codex mới bảo trì bản stable mà không làm lại các phase đã hoàn tất. Phase 11 là research-only và không merge; Phase 14–15 giữ candidate tooling; Phase 16 đã qua Premiere XML/PCM và production-adopt transition bảo thủ.

## Định hướng và vị trí dự án

- Workspace thật: `F:\RIN APP\App-Auto-Edit_codex_2`.
- Private GitHub repo: `tranvietthang94-jpg/premiere-auto-dialogue-xml`.
- Phase 00–09 đã merge vào `main`. Phase 09 qua PR `#12`, merge commit `50ee4f9fb14d58e1dffbd1d23b807f2d58802976`; CI hậu merge `31461869682` đạt.
- Phase 10 qua PR `#14`, merge commit `19c6577f38b25a314e058b35c59f3219b4cfa4a8`; CI hậu merge `31691393143` đạt. Phase không tạo release mới.
- Phase 12 qua PR `#17`, merge commit `9d3abf55ea6a0231da2b064b5e60251777d64087`; CI hậu merge `32553652980` đạt.
- Phase 13 đã đạt; implementation qua PR `#18`, merge commit `ad89d2a38488ecfb05d9f41ad18085d9359f494b`; PR CI `32562437223` và hậu merge CI `32562657372` đạt. Từ 2026-08-23, GitHub chỉ lưu source code; installer dùng thật chỉ được tạo và giữ cục bộ. Source-only PR `#19` CI `32645714062` đạt toàn bộ gate.
- Phase 14 đạt phạm vi nghiên cứu/candidate một-boundary qua PR `#20`; CI `32703259587` đạt `213/213` test, policy `8/8`, publish/installer smoke. A2 Constant Gain 0 dB v3 giảm boundary khoảng `61,97 dB`, round-trip khớp fixture thủ công `0` mismatch và giữ M19; production writer vẫn không chèn transition.
- Phase 15 đạt phạm vi candidate/tooling nhiều boundary trên PR `#21`: Premiere round-trip giữ 12 transition với đúng 48 normalization, 12/12 boundary giảm step `31,63–61,97 dB` và M19 còn Enabled. Production adoption bị từ chối vì một phrase A2 render `-8,7991 dBFS` thay vì mục tiêu `-6 dBFS`; app mặc định vẫn không chèn transition.
- Phase 16 đạt trên PR `#22`; PR CI `32802975587` đạt toàn bộ build/test/policy/publish/installer smoke. Source-peak safety gate loại boundary khi bỏ một frame quanh transition làm expected peak lệch quá `0,1 dB`, không tự bù gain. Premiere giữ đúng 12 transition/48 normalization; năm track đạt `1.668/1.668` phrase PCM, 12/12 boundary giảm step `26,58–48,94 dB`, M19 không đổi. Production app đã adopt tối đa 12 transition fail-closed, audit `2.0`; HGE2 production selection hash trùng candidate và semantic mismatch `0`.
- `v0.1.0 Stable` khóa baseline Phase 00–16. Rà soát cuối từ `fc6ead94ccc3a8434c9e0aecb9ed87d4768f0995` đạt build sạch, `223/223` test, policy 24/30 `8/8`, policy multi-boundary, publish `411` file, format/diff sạch và NuGet vulnerability scan không có advisory. GitHub chỉ giữ source/tag/release notes; installer stable được ký, smoke-test và lưu local từ đúng post-merge commit.
- Chủ dự án không muốn preview/review UI; app chỉ tập trung xử lý âm thanh và xuất XML. Phase 10 đã đạt mọi gate logic, HGE, Premiere round-trip và CI/installer.
- RC1 ký nội bộ lịch sử vẫn được giữ cục bộ trong `private-artifacts/release-v0.1.0-rc1`; draft release GitHub đã được xóa theo chính sách source-only. `v0.1.0 Stable` thay RC1 làm baseline dùng thật nhưng không ghi đè hoặc xóa gói lịch sử.
- Tài liệu phase đầy đủ nằm trong `docs/`; đọc trước `README.md`, `docs/IMPLEMENTATION_PLAN.md`, `docs/PHASE00_RESULT.md`, `docs/PHASE06_PILOT_RESULT.md`, `docs/PHASE07_INSTALLER_RELEASE.md`, `docs/PHASE08_REVIEW_QUEUE.md`, `docs/PHASE09_AUDIO_XML_HARDENING.md`, `docs/PHASE10_NOISE_BOUNDARY_STABILITY.md`, `docs/PHASE12_PREMIERE_INPUT_COMPATIBILITY.md`, `docs/PHASE13_RELIABILITY_PERFORMANCE.md`, `docs/PHASE14_CLICK_SAFE_BOUNDARY_RESEARCH.md`, `docs/PHASE15_MULTIBOUNDARY_CLICK_SAFE.md`, `docs/PHASE16_CONSERVATIVE_TRANSITION_ADOPTION.md`, `docs/INTERNAL_CODE_SIGNING.md`.

## Sản phẩm đã khóa

- App WPF/C#/.NET 10 chạy độc lập, UI tiếng Việt, offline hoàn toàn; không Python, cloud, API, telemetry hay tải model sau cài.
- Input là Premiere Final Cut Pro XML; app đọc đúng source range WAV được dùng trên timeline, phân tích audio và tạo XML/audit mới. Không sửa XML, WAV hay `.prproj` gốc.
- Mỗi track là một nghệ sĩ. Speech được giữ/level; noise hoặc bleed rõ ràng được `enabled=FALSE` chứ không xóa; ambiguous luôn Enabled và có marker `Cần kiểm tra`.
- Target người dùng chọn là phương án B: sample peak từng phrase gần `-6 dBFS` sau routing mono-center/equal-power của Premiere. Đây không phải LUFS, true peak, limiter hoặc cam kết Master bus.
- Boost tổng tối đa `+18 dB`; phrase cần hơn bị cap và đánh marker. App luôn tạo sequence/XML mới có hậu tố `- AUTO AUDIO`.
- Preset Cân bằng: VAD 0.50, speech tối thiểu 120 ms, nghỉ tách câu 350 ms, padding trước/sau 200/300 ms, direct speech cao hơn adaptive noise khoảng 10 dB, tối đa 4 worker.
- Model Silero VAD `6.2.1`, SHA-256 `1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3`, MIT.
- MVP đã kiểm chứng cho sequence 25 fps, stereo master, WAV mono PCM 48 kHz 16/24/32-bit, source trim, gap, file nối tiếp và phrase qua ranh giới WAV. Từ chối overlap cùng track, retime, nested/multicam, effect, automation, submix, media thiếu hoặc format không hỗ trợ.

## Phase 00 — hợp đồng Premiere XML/Gain

- Premiere Pro 2026 round-trip đạt timing, source trim, ticks, Disable và gain trong `±0.1 dB`.
- Một Audio Levels dùng được từ giảm gain đến `+12 dB` với `value = 10^(dB/20)`.
- Hai Audio Levels cùng loại đặt trực tiếp trong XML không cộng ổn định; Premiere thường chỉ giữ effect cuối. Một Audio Levels > `+12 dB` có thể nghe đúng một lượt nhưng re-export về `+12 dB`; không dùng encoding đó.
- Premiere Gain filter thủ công có effectid `{61756678, 4761696e, 4b657947}` và parameter `Gain(dB)`, nhưng `<value>` vẫn là hệ số tuyến tính, không phải literal dB. Literal `3` thành khoảng `+9.542 dB`.
- Encoding đã chốt: `<= +12 dB` dùng một Audio Levels; `> +12` đến `+18 dB` dùng Audio Levels cố định `+12 dB` (`3.981071706`) cộng Premiere Gain filter remainder, cũng ghi `10^(remainderDb/20)`.
- Không được đổi encoding này nếu chưa có Premiere XML→import→PCM→re-export evidence mới.

## Phase 01–05 — implementation

- Solution gồm App/WPF, Core XML/media, Audio/VAD, Output writer và Validation; SDK khóa ở `.NET 10.0.302`. Có SDK cục bộ ignored tại `.tools\dotnet\dotnet.exe`.
- WAV parser đọc streaming/64-bit offset, dùng header thật, hỗ trợ PCM/WAVE_FORMAT_EXTENSIBLE, file gần 4 GB và chỉ chấp nhận tail thiếu 1–3 byte sau frame hoàn chỉnh; không sửa media.
- Parser/writer giữ video/sequence settings/track layout/routing/media references, sinh ID/UUID mới, split fragment đúng `start/end`, `in/out`, `pproTicksIn/Out`, source-track.
- Audit ghi SHA nguồn/kết quả, model/checksum, thresholds, track/file/source/timeline range, trạng thái speech/noise/bleed/ambiguous, peak/gain/cap/marker và warning.
- Cancel hoặc lỗi không để XML dở; Dừng an toàn trên app thật đã chứng minh thư mục output vẫn trống và input hash không đổi.
- Publish self-contained kiểm apphost, hostfxr, hostpolicy, CoreCLR, WPF, ONNX native, model checksum, notice/license, không Python/PDB; payload hiện có 410 file.

## Phase 06 — pilot HGE2 và quyết định quan trọng

- Fixture thật: `F:\demo\test HGE2.xml` và media `F:\demo\F2`; 7 track A1–A7, sequence 53,760 frame ở 25 fps (`00:35:50:10`). Private evidence trong `private-artifacts/phase06-*`; không commit media/ảnh/audit đầy đủ.
- Lượt PCM đầu phát hiện offset chung `-3.0103 dB` do Premiere center-pan/downmix. Người dùng chọn phương án B: cộng compensation `+3.0102999566 dB` để target hậu routing gần `-6`, nhưng trần boost vẫn `+18`.
- Candidate B đầu tiên có một phrase nóng khoảng `0.97 dB` vì audit đo lõi speech còn fragment XML được căn theo frame video. Fix frame-safe: peak reference là max giữa direct-speech core và toàn bộ frame speech/ambiguous-near-speech Enabled của phrase.
- Candidate audit `1.2` đạt PCM A1–A7. Sau review Context, người dùng chỉ nghe/ghi nhãn cột timecode bên trái và dùng L/N/M + note cho cả Context; các nhãn này không phải nhãn Target cô lập nên không được dùng tính 100%/90%/0%.
- Note thực tế phát hiện M19/A3 tại `00:07:28:15` là lời bị Disable dù VAD thấp. Fix audit `1.3`: chuỗi VAD thấp nhưng năng lượng cao liên tục >=120 ms và mic đích chiếm ưu thế được giữ thành `ambiguous`, Enabled, marker `Cần kiểm tra`; transient ngắn vẫn noise.
- Candidate audit `1.3` giữ M19 và được Premiere round-trip lại A1–A7: 2,132/2,132 phrase khớp gain/timing; 935/935 phrase không cap ở target; 1,197 phrase cap khớp dự đoán, không phrase cap nóng hơn `-6`; sai lệch source-linked lớn nhất < `0.000027 dB`.
- Premiere không hiện FCP Translation Results; bật lại clip Disable nghe được audio nguồn. Kết luận chỉ là import/recovery đạt, không được giả lập một report Premiere không tồn tại.
- User miễn cổng nghe Target ngày 2026-08-04. Tuyệt đối không tuyên bố đã đạt `100% direct speech`, `>=90% noise/bleed Disable` hoặc `0% ambiguous Disable` theo nhãn người nghe. Ambiguous trong logic vẫn phải được giữ.
- App/publish đã phân tích thành công trên máy Windows 11 khác và máy ảo Windows 10 x64 mới cài. Khi VM chỉ có XML, app báo đúng 7 `media-missing` và khóa Analyze; sau khi chép đủ WAV tham chiếu, phân tích bình thường. Đây là hành vi đúng, không phải lỗi logic.

## Phase 07 — installer, signing, release

- Installer dùng Inno Setup `7.0.2` x64, AppId ổn định `{99295721-E0F1-4299-85FF-51B020C49600}`.
- Per-user install: `%LOCALAPPDATA%\Programs\Premiere Auto Dialogue XML`; `PrivilegesRequired=lowest`, Windows 10/11 x64, không admin. Có Start Menu, tùy chọn Desktop, uninstaller; dữ liệu người dùng không bị xóa.
- Build source: `installer/PremiereAutoDialogueXml.iss`, `scripts/build-installer.ps1`, `scripts/test-installer.ps1`, `scripts/package-internal-signed-release.ps1`.
- CI Windows build unsigned vì không được nhận private key; CI restore/build/93 tests/publish, tải Inno Setup pin 7.0.2 và verify attestation, build/cài/mở/gỡ installer rồi upload artifact. Main CI sau merge: run `30881740990`, success.
- Bản phát hành nội bộ tự ký: subject `CN=Premiere Auto Dialogue XML Internal`; RSA 3072/SHA-256/EKU Code Signing; thumbprint `025AEBC4AA90E0D5F85658082952A7E029CF467A`; hết hạn `2031-08-04T05:30:15Z`; không có public timestamp.
- Private key `NonExportable`, chỉ trong `Cert:\CurrentUser\My` của Windows profile phát triển hiện tại; export policy `None`. Không có PFX/private key trong repo, CI, artifact hoặc release. Nếu mất máy/profile phải tạo identity mới và trust lại máy đích.
- Public CER SHA-256: `105EAA41A6D4C757A84C7867938ABFF2DB286B82D4940618038A324C1E7F7221`. Máy đích cần cài CER vào Current User `Trusted Root Certification Authorities` và `Trusted Publishers`; user đã xác nhận hiện đúng signer.
- Final installer từ `main` merge commit: SHA-256 `9C3BAE324D868AFAF6EA0F726ECB910EB67F4CFA9E8128095B9CFED177536130`; installer/app/uninstaller đều Authenticode `Valid`; 410/410 payload verified; install/open/uninstall đạt; user-created sentinel sống sau uninstall.
- Final local package: `F:\RIN APP\App-Auto-Edit_codex_2\private-artifacts\release-v0.1.0-rc1\PremiereAutoDialogueXml-internal-signed-20260804-054755-1048bec120e043d3bb820609b2adb83a`.
- Draft release có 9 asset: installer, CER, certificate/installer/test/internal manifests, 2 hướng dẫn và `SHA256SUMS-INTERNAL.txt`. Release đang draft/prerelease; chưa public.
- Phase 08 thêm shadow evidence đa mic chỉ để tư vấn, audit schema `1.4` và CSV review tiếng Việt được gom nhóm/xếp ưu tiên. Shadow evidence không thay `Status`, `Enabled`, gain, marker hay XML audio.
- Package writer phát XML theo streaming để full HGE không giữ cây XML hàng trăm MB trong RAM. Pilot cuối: HGE2 `42,7 giây`/`174,2 MB`; full HGE `25 phút 6 giây`/`785,6 MB`, dưới cổng 90 phút/1,5 GB.

## Phase 09 — candidate làm chắc audio/XML

- Commit triển khai: `a04b9f6` thêm FIR chống alias; `16cc369` thêm shadow merge bảo thủ, audit `1.5` và validator trước writer; `8e989d2` cho phép phrase supersession do căn frame chỉ khi toàn bộ lõi vẫn Enabled.
- FIR streaming 127 tap/Blackman/cutoff 7 kHz/decimation 3 đạt block invariance, passband `<0,05 dB`, stopband tone 12 kHz `>60 dB`; không đổi model Silero 6.2.1, preset, gain/routing hoặc XML encoding Phase 00.
- Default analyzer chạy cả legacy stride-3 và anti-alias FIR. Legacy Enabled/candidate Disabled luôn thành ambiguous Enabled; candidate có thể mở thêm vùng legacy Disabled. `OutputDecisionContractValidator` dừng trước publication nếu status/Enabled, coverage, phrase gain/target/cap hoặc marker không nhất quán.
- PCM A1 từ HGE2 candidate đầu xác nhận Premiere áp đúng source-linked gain/timing 287/287 phrase, nhưng loại candidate vì 11 `legacy-safety` phrase không cap chỉ chứa mẩu 1–32 frame, kế thừa peak không thuộc output fragment và trượt target. XML `DE06C...`/audit `16C154...` không còn hợp lệ để test.
- Fix giữ trọn phrase component legacy cùng gain khi có disagreement; candidate-only trong component được giữ ambiguous unity-gain. Merger có postcondition coverage và 2 regression mới; Release đạt `122/122` test.
- HGE2 replacement `phase09-hge2-pilot-20260809-4` đạt `84,200 giây`/`203,3 MB`, 0 error, 2.183 phrase/10.831 fragment/5.219 marker. XML SHA-256 `A11D046019BDF0F8677E9E952FE45DE7489DF3C079F578CAABCDF3F38FE57AA3`; audit SHA-256 `D62901F88A3A5EDC380D548FE43B2440D7CAC1C77A24F739E525B43C91519C7A`.
- Comparator replacement vs Phase 08: source hash khớp, coverage mismatch 0, legacy Enabled bị mất `0 interval/0 frame`, giữ thêm 2.424 interval/8.333 frame, temp 0; M19/A3 11214–11218 Enabled. Đây là candidate duy nhất để import/retest A1.
- Premiere PCM round-trip của replacement đạt A1–A7: 2.183/2.183 phrase khớp timing/gain; 940/940 uncapped ở `-6,000019084..-5,999973633 dBFS`; 1.243/1.243 capped khớp dự đoán, capped nóng nhất `-6,004387657 dBFS`; max audit/source-linked delta dưới `0,000027 dB`. User xác nhận M19 vẫn Enabled và nghe được.
- Full HGE cuối `phase09-full-hge-pilot-20260809-2` đạt `38 phút 33,1 giây`/`1.065 MB`, 0 error, 43.603 phrase/247.515 fragment/126.485 marker. XML `0E8D67...`, audit `0BC66E...`; comparator với Phase 08 có source hash khớp, coverage mismatch 0, mất legacy Enabled `0 interval/0 frame`, giữ thêm 55.177 interval/189.571 frame, temp 0.
- Publish giữ 410 payload; installer unsigned private SHA-256 `225AD65E68D6806AC8426E4C39A8907CDD527E431E2154A7C0BA8A18A3C35CC8` qua cài/mở/xác minh 410/410/gỡ/sentinel/registry. Nó không thay RC1 và không được phát hành.
- GitHub Actions PR run `31317023545` trên commit validator round-trip `20cfb1a` đạt trong 3 phút 6 giây: restore/build/122 test/publish/Inno Setup/installer/upload đều xanh.
- Premiere XML re-export SHA-256 `51E69A2B255E451EDA6D267F05AB3CABFA4301801528B2247CEC46491F9BE3E0` đạt report `phase09-roundtrip-compatible` SHA-256 `1D60F50FF06D98D34607AD4480BEA136BDFF7A072BA927C2693C9E0CFC7E7A3B`: 10.831/10.831 clip, 7.025 Enabled, 3.806 Disabled, timing/source/media khớp, max gain delta `0,000055244 dB`, 5.219 marker giữ nguyên semantic. Premiere đổi thứ tự marker và normalize 3.783 Gain filter thành Audio Levels đúng hành vi Phase 00.
- `scripts/phase09/Test-Phase09RoundTrip.ps1` là validator lặp lại được cho input/result XML. Phase 09 đã `passed` và merge vào `main` tại `50ee4f9`; CI hậu merge `31461869682` đạt trong 2 phút 53 giây.

## Phase 10 — noise floor và ranh giới câu

- Slice 10A đã triển khai trên `codex/phase10-noise-boundary-stability`: mode `Phase09Baseline`/`NoiseBoundaryCandidate`, per-frame noise/boundary trace, comparator theo frame/phrase/interval và corpus tổng hợp không chứa dữ liệu riêng tư.
- Candidate 10A cố ý giống hệt baseline. Chỉ `AnalyzeShadow` chạy hai pipeline/capture trace; đường `Analyze` mà app dùng vẫn chạy một lần theo Phase 09 và không gắn trace vào output. Vì vậy 10A không đổi status, Enabled, gain, marker, audit hoặc XML.
- Corpus bao phủ room tone, floor step-up/down, silence, loud-first, VAD-negative high-energy conflict, speech sát ngưỡng, jitter, media gap và clip boundary. Comparator có test phát hiện `baseline Enabled → candidate Disabled` giả lập.
- Baseline trace khóa hai khoản nợ cần sửa ở 10B: loud-first-frame hiện tự đặt floor theo chính nó; high-energy VAD-negative conflict hiện vẫn training-eligible. Không sửa hoặc diễn giải lại chúng trong 10A.
- Release đạt `130/130`; build sạch warning/error; format verify sạch. Chưa chạy HGE2/full HGE/Premiere vì chưa có XML candidate.
- Slice 10B thêm policy `phase10-background-eligible-p20-v1`: P20/window 512, warm-up 8 frame, rise `0,05`, fall `0,20`; stable step-up chỉ promote sau 16 frame liên tục/spread tối đa 3 dB.
- Candidate đánh giá frame bằng floor trước update; VAD speech/no-media/non-finite/high-energy conflict không được học. Warm-up high-energy thành `ambiguous-noise-floor-warmup` Enabled; floor luôn finite trong `[-144,0]`.
- Trace ghi policy/readiness/eligibility/floor trước-sau; comparator từ chối policy provenance sai. Step-response rise/fall đơn điệu và bounded; Release đạt `137/137`; format sạch.
- Slice 10C khóa `phase10-vad-start050-continue040-v1`: start `0,50`, continue `0,40`, break `350 ms`, start/direct evidence vẫn `120 ms`. No-media đóng context; gap đúng 350 ms không bridge.
- Continue không được cộng vào start/direct evidence. Context thiếu direct giữ `ambiguous-vad-continue-context-near-speech`, Enabled và cùng phrase/gain; không có start thì không tạo phrase.
- Trace/comparator có boundary policy provenance và state start/continue riêng. Synthetic khóa `.40` accept/`.39` reject, jitter, evidence minimum, gap/media; Release đạt `146/146`; format sạch.
- Candidate 10C vẫn chỉ chạy trong `AnalyzeShadow`; app/output vẫn dùng Phase 09, không có audit/XML candidate hoặc Premiere pilot mới. Bước tiếp theo là Slice 10D project shadow + audit `1.6` + conservative merge/validator.
- Slice 10D đã nối project production theo hai lớp fail-safe: Phase 09 baseline và Phase 10 candidate được merge bảo thủ riêng trên từng `LegacyStride3`/`AntiAliasFir`, sau đó kết quả tiếp tục qua merge front-end Phase 09. Mọi baseline Enabled được giữ; disagreement thành Ambiguous/Enabled và whole-phrase fallback giữ nguyên coverage/gain.
- Audit schema `1.6` ghi policy provenance, floor/eligibility/frame differences, phrase boundary/split/merge với gain/cap, decision baseline/candidate/final. Validator tự tính lại comparison và dừng trước khi tạo run directory nếu shadow thiếu, sai policy/cùng-observation/count/gain hoặc làm mất baseline Enabled.
- Slice 10D đạt `150/150` test; build/format sạch. Chưa có HGE2/full HGE/Premiere/publish candidate mới. Bước tiếp theo là 10E pilot trên artifact directory mới; nếu XML đổi phải dùng đúng hash đó cho PCM A1–A7, M19 và Premiere XML re-export.
- Slice 10E giới hạn audit frame evidence bằng full-stream SHA-256 + summary + mẫu chẩn đoán bounded; long timeline dùng một worker; phrase interval index và one-pass correlation giảm CPU/RAM. Audit compact JSON được hash trong lúc ghi và hash lại từ đĩa, không deserialize thêm cây audit lớn.
- XML frame run được gộp theo semantic audio thực: Disabled liền nhau; Enabled cùng phrase/gain. Aggregate ưu tiên Ambiguous để giữ review bảo thủ. Release đạt `153/153`; build sạch.
- HGE2 cuối `phase10-hge2-pilot-20260813-1`: `78,356 giây`, peak `260,9 MB`, 2.136 phrase/8.291 fragment/5.207 marker; XML `6F855699ED6D39712F3118A661DCF18943750F805D3EDB662546D033A808910E`, audit `21A18FB7799D171AB9BCD744B65D1ECEBF3E9EA8DE9321B0C707476AAAD6E534`. So Phase 09: coverage mismatch 0, lost Enabled 0, newly Enabled 2.205 interval/7.388 frame, temp 0; M19/A3 11214–11218 Ambiguous/Enabled.
- Full HGE cuối `phase10-full-hge-pilot-20260811-8`: `74 phút 56,5 giây`, peak `1.320,4 MB`, XML 204,34 MB `627BA7EEEF777F4C8A2DF50FB551FD4B5AA9BEC71BE0D866A97D81066B6420FB`, audit `A77E44B8C52547A0DC748C6E6BB8C328B988014D120672DB83BE847FC8FEBE51`; source hash khớp, coverage mismatch 0, lost Enabled 0, newly Enabled 47.305 interval/157.237 frame, temp 0.
- Người vận hành import đúng HGE2 XML `6F855...` và xác nhận M19/A3 vẫn Enabled. PCM A1–A7 đều mono 48 kHz/24-bit, đủ 103.219.200 sample; validator đạt 2.136/2.136 phrase, 921/921 uncapped ở target và 1.215/1.215 capped khớp dự đoán. Max audit delta `0,000026641 dB`, source-linked delta `0,000026615 dB`.
- M19 PCM frame 11214–11218 có 7.680/7.680 sample khác 0, correlation nguồn `0,999999999960662`, gain `-3,010296076 dB` đúng routing; report `C591B87D...` đạt `m19-rendered-audio-preserved`.
- Premiere re-export `Untitled test.xml` SHA `C7F83F25...` bọc đúng một sequence trong project. Validator round-trip `1.1` được harden để nhận direct sequence hoặc đúng một top-level project sequence; report `E3E38CA9...` đạt 8.291/8.291 clip, 4.488 Enabled, 3.803 Disabled, 5.207 marker và max gain delta `0,000055244 dB`.
- CI `31689521606` trên app candidate `a4ac4a7` đạt build/test, self-contained publish và installer smoke trong 3 phút 1 giây. Phase 10 đạt; RC1 hiện hành không đổi.

## Lỗi đã gặp và cách tránh

1. Gain XML: không stack hai Audio Levels cùng loại và không ghi literal dB vào Gain filter. Dùng encoding Phase 00.
2. Routing -3.0103 dB: target phải là post-routing Option B; không bỏ compensation hoặc tăng boost cap.
3. Frame rounding: đo chỉ lõi speech làm một phrase nóng; giữ policy frame-safe max core + Enabled phrase frames.
4. VAD false negative M19: giữ conflict VAD thấp/năng lượng cao liên tục thành ambiguous; note thực tế người dùng rất có giá trị dù nhãn Context không dùng làm metric.
5. Missing media trên VM: XML không mang WAV; phải chép đúng media references. Lỗi `media-missing` và nút Analyze disabled là fail-safe đúng.
6. Installer test harness đầu tiên duyệt sai registry path và để lại một key thử nghiệm. Đã xóa sau khi xác minh exact target; harness giờ chỉ dùng AppId ổn định, recovery uninstall và kiểm tra xóa HKCU entry.
7. UI installer ban đầu còn `Browse`, disk-space và `Destination location` tiếng Anh. Đã bổ sung `ButtonWizardBrowse`, `DiskSpace*`, `ReadyMemo*`; trang chính đã kiểm tra trực quan tiếng Việt.
8. GitHub artifact ban đầu lồng thư mục run. Đây là lỗi lịch sử; từ Phase 13 workflow không còn upload installer lên GitHub.
9. `Import-Certificate` từng treo khi trust self-signed root, tạo trạng thái dở. Đã xóa đúng cert/output partial và đổi script sang `certutil.exe -user -f -addstore Root/TrustedPublisher`.
10. SignTool không ký app ở payload path quá dài (`File not found`). Fix: ký app bằng `Set-AuthenticodeSignature` trước khi tạo publish manifest/ZIP; Inno SignTool chỉ ký Setup/uninstaller ở path ngắn. Không chuyển việc ký app xuống sau manifest vì sẽ làm hash payload stale.
11. Self-signed không tự tạo SmartScreen reputation và chưa trusted trên máy mới. Không gọi đây là chữ ký công cộng, không tuyên bố “virus-free”. Luôn phát hành checksum/provenance và hướng dẫn trust nội bộ.
12. Inno Setup hiện yêu cầu xem xét commercial license nếu phát hành thương mại; dự án hiện private/internal. Recheck license trước khi thương mại hóa.
13. Full HGE từng đạt peak `5,247 GB` do giữ đồng thời hai cây XML DOM; bỏ cây đọc lại còn `1,849 GB` vẫn chưa đạt. Fix đúng là generation plan + streaming writer từng fragment, không hạ cổng RAM; lượt cuối còn `785,6 MB` và XML/audit semantic không đổi.
14. CI Phase 12 từng đỏ sau khi build/test/publish/installer smoke đều đạt vì GitHub artifact storage đầy. Quyết định Phase 13 loại hẳn upload/cleanup installer artifact và quyền `actions: write`; CI vẫn build/smoke-test bản tạm trên runner, còn installer dùng thật chỉ giữ cục bộ.

## Quy tắc tiếp tục ở phiên mới

- Bắt đầu bằng `git status`, `git log`, đọc docs phase; giữ nguyên user changes và private artifacts.
- Tạo branch phase mới từ `main`; draft PR, commit nhỏ, push thường xuyên; merge commit chỉ sau test/evidence.
- Không commit WAV, `.prproj`, output, log đường dẫn nhạy cảm, `.cer/.pfx/.key`, installer hoặc private artifacts. `.gitignore` đã chặn các loại này.
- Không thay input/output in-place. Mỗi run/build/package phải dùng thư mục mới và không overwrite.
- Dùng `.\.tools\dotnet\dotnet.exe test PremiereAutoDialogueXml.slnx --configuration Release --no-restore` nếu PATH không có SDK.
- Signed build chỉ trên đúng Windows profile giữ cert:
  `.\scripts\build-installer.ps1 -OutputRoot <new-root> -SigningCertificateThumbprint 025AEBC4AA90E0D5F85658082952A7E029CF467A`.
- Sau signed build luôn chạy `scripts/test-installer.ps1` với `-ExpectedSignerThumbprint`, rồi package bằng `scripts/package-internal-signed-release.ps1`; kiểm manifest `privateKeyIncluded=false`.
- Nếu thay logic audio/XML, bắt buộc tạo audit mới và Premiere round-trip evidence tương xứng; không tái gắn PCM cũ sang XML/audit hash mới.
- Ưu tiên nhu cầu thực tế của user: app xử lý đầu vào tốt, editor vẫn nghe/kiểm tra nội dung. Giữ vùng mơ hồ thay vì tự động mute khi bằng chứng xung đột.

## Trạng thái bàn giao

Phase 00–10 hoàn tất trên `main`. Phase 10 dùng merge bảo thủ hai tầng, audit `1.6` và output bounded; HGE2/full HGE, M19, PCM A1–A7, Premiere XML re-export và CI/installer đều đạt. Phase 11 calibrated multi-mic bleed đã hoàn tất như nghiên cứu shadow-only trên branch riêng nhưng không được adopt/merge; `main` không chứa logic Phase 11.

Phase 12 mở ngày 2026-08-14 từ clean `main` commit `3f41d7cd2d8b4d90241b4bd2f50f798e23d65796` trên branch `codex/phase12-premiere-input-compatibility` và đã merge PR `#17` vào `main` tại `9d3abf55`. Cổng đầu nhận sequence nguyên `24/25/30 fps NDF`; source mono PCM `48 kHz`, master stereo, routing/gain/Enabled/M19 vẫn giữ Phase 10. Stereo source, sample rate/routing mới, `23,976/29,97` và drop-frame chưa mở. Slice 12A–12C dùng `PremiereNdfFrameGrid` xuyên parser, analysis, DOM/streaming XML, review, audit và validator; audit `1.8` ghi timing policy `phase12-integer-ndf-exact-frame-grid-v1`, PCM evidence `1.2`. Slice 12D: HGE2/full HGE 25 fps đều coverage mismatch `0`, lost Enabled `0`, newly Enabled `0`, M19 Enabled, temp `0`; full HGE `77 phút 02 giây`, peak `1.280,5 MB`; installer smoke đạt `410` payload file. Slice 12E: Premiere Pro 2026 tạo PCM mono 48 kHz/24-bit và XML re-export mới cho 24/30; mỗi rate đạt `2/2` phrase, `6/6` clip, `4 Enabled`, `2 Disabled`, `4` marker, max gain delta `0,0000138 dB`. Premiere chỉ normalize sequence depth metadata `24 → 16`; round-trip strict mặc định vẫn từ chối, Phase 12 chỉ cho phép explicit normalization `sequence-depth-24-to-16`. Adoption reports đạt `phase12-integer-ndf-premiere-artifacts-compatible`; không tạo release mới.

Phase 13 mở ngày 2026-08-22 từ clean `main` commit `9d3abf55ea6a0231da2b064b5e60251777d64087`, đã merge PR `#18` vào `main` tại `ad89d2a38488ecfb05d9f41ad18085d9359f494b`; PR CI `32562437223` và hậu merge CI `32562657372` đạt. `scripts/phase13/Test-PremiereRoundTripPolicy.ps1` khóa 8 case 24/30 trong CI: strict từ chối depth `24 → 16`, explicit chỉ chấp nhận đúng normalization đó và vẫn từ chối depth/Enabled drift. Paired scan đọc/giải mã PCM một lần cho hai detector state độc lập; audit `1.9` giữ full count/SHA-256 và tối đa 64 sample mỗi stream chẩn đoán; validator vẫn kiểm fail-safe baseline Enabled. HGE2 `78,567 giây`/`276,2 MB`/audit `9,45 MB`; full HGE `75 phút 52,4 giây`/`1.218,3 MB`/audit `157,52 MB`. Cả hai comparator đều source hash khớp, coverage mismatch/lost Enabled/newly Enabled/temp bằng `0`; M19/A3 11214–11218 vẫn Ambiguous/Enabled. Hai worker và staged 2→1 bị loại vì peak lần lượt `1.601,5` và `1.430,4 MB`; production giữ một worker cho long timeline. Release build sạch, `201/201` test, publish `410` payload và installer smoke cài/mở/xác minh/gỡ đều đạt; installer unsigned chỉ là quality gate, không đổi RC1 hoặc semantic audio/XML. Sau cleanup account-wide, Billing vẫn báo GitHub Free đã dùng `0,5/0,5 GB` Actions storage trong kỳ; chủ dự án chọn chính sách source-only ngày 2026-08-23. Workflow bỏ upload/cleanup artifact và quyền ghi Actions, CI chỉ giữ installer tạm đến khi runner kết thúc; installer thật nằm ngoài Git trong `private-artifacts/` hoặc `artifacts/`. Source-only PR `#19` CI `32645714062` đạt toàn bộ gate mà không tạo artifact.

Phase 14 mở ngày 2026-08-24 từ clean `main` commit `90e8e2d81a2a20903a349f50bec10b953a8794c5` trên `codex/phase14-click-safe-boundary-research`. Scanner nguồn v2 lọc HGE2 từ `8.176` state/gain transition xuống `1.282` transient candidate; bảy PCM Premiere baseline có `982` candidate. Operator xác nhận ba excerpt đều có click nhẹ. Fixture Premiere khóa Constant Gain `KGAudioTransCrossFade0dB` một frame. V2 pre-normalize giảm click nhưng bị reject vì re-export mở source handle lần hai. V3 chỉ chèn transition, để Premiere normalize đúng một lần: re-export SHA-256 `56E65218F63216A0226CA89245A4C645405D71F473EC51211F51996D1D9160C9` khớp fixture thủ công với `0` mismatch; WAV mono 48 kHz/24-bit giảm A2 từ `-6,1826` xuống `-68,1484 dBFS`, M19/A3 11214–11218 vẫn Enabled. PR `#20` CI `32703259587` đạt `213/213` test, policy `8/8`, publish/installer smoke. Phase đóng ở mức research/candidate tooling; production writer và app mặc định không chèn transition. Rollout nhiều boundary phải có conflict policy và Premiere corpus riêng cho Enabled→Disabled, Disabled→Enabled và gain change.

Phase 15 mở ngày 2026-08-24 trên `codex/phase15-multiboundary-click-safe`. Planner bounded chọn 12 boundary HGE2 gồm 3 Enabled→Disabled, 5 Disabled→Enabled và 4 gain change; writer kiểm provenance/kind/source handle/conflict trước khi tạo candidate. Candidate so generated XML Phase 13 có mismatch 0 trên 8.291 clip, 4.488 Enabled, 3.803 Disabled, 5.207 marker và gain. Premiere re-export SHA-256 `D55443442BB6CD3603E0C55390429C8D44F5591ECC46F02C13A23046CF4A5C98` giữ đúng 12 transition và chỉ tạo 48 normalization một-lần đã khóa; round-trip report SHA-256 `E6B77A472338F9B44D6BF51CDB8834A95436FDE2F66D8758047D3178A525457E`. PCM A2/A3/A6/A7 cho thấy 12/12 boundary giảm step `31,6284–61,9658 dB` và không còn transient candidate; M19 vẫn Enabled/có audio. Tuy nhiên phrase A2 `T02-P000010-legacy-safety`, frame 11140–11161, render `-8,7990888 dBFS` thay vì dự đoán `-6,0000003 dBFS`, lệch `-2,7990885 dB`. Vì gain gate không đạt, Phase 15 không nối transition vào production writer; app mặc định giữ semantic Phase 13. Candidate CLI/validator/policy test được giữ fail-closed. Nếu nghiên cứu tiếp phải dùng transition-aware gain planning hoặc exclusion policy và tạo Premiere render mới; không cần export thêm để đóng Phase 15.

Phase 16 mở ngày 2026-08-24 từ `main` commit `d3d0d4bf56efcca8f144fee800d9528c6ddb203d` trên `codex/phase16-conservative-transition-adoption`, draft PR `#22`. `PremiereTransitionGainSafetyGate` đọc source PCM phrase, bỏ guard một frame mỗi phía Enabled và chỉ cho qua khi retained expected peak lệch không quá `0,1 dB`; thiếu phrase/gain/source hoặc source peak không khớp audit đều fail-closed. `PremiereConstantGainSafeBatchPlanner` thêm one-transition-per-phrase conflict. HGE2 scan có 1.282 transient, capture 785: 633 Eligible, 150 thiếu phrase provenance và 2 expected-peak-loss bị loại. A2/frame 11140 bị loại với retained delta `-10,1472188 dB`; A3/frame 27268 bị loại `-3,5084822 dB`. Candidate chọn 12 transition (5 E→D, 6 D→E, 1 gain), XML SHA-256 `2F3FBD0DEEEFE3AA51A30A30141631A39435DE8378F38650DD646DA203DBAB54`. Premiere re-export SHA-256 `BD7E2377ED637486B61587A48D08C48407341FEF5A6B71FFB186E52CBD68ED1F` giữ 12 transition, đúng 48 normalization, 8.291 clip/4.488 Enabled/3.803 Disabled/5.207 marker; năm WAV A2/A3/A5/A6/A7 đạt `1.668/1.668` phrase, 12/12 boundary giảm step `26,58–48,94 dB`, M19 A3 không đổi. `ProductionTransitionPackageAdopter` chạy tự động trước khi app trả output, chọn tối đa 12 và ghi audit `2.0`; lỗi/cancel xóa run dở. HGE2 production chạy `126,9 giây`, peak `284,1 MB`, XML SHA-256 `84CD04782CEB33FBA71026FC3148284D9C5734EA9ED8066891391A473659642F`, selection SHA-256 trùng candidate `FB92805104949870B015F319F16FD0BF55CEA812ABF5CF5876C475137143F874`, semantic comparator mismatch 0 và temp 0. Phase không đổi VAD/noise/bleed/ambiguity/gain/routing/marker/UI flow/RC1.

Stable closeout mở ngày 2026-08-26 trên `codex/v0.1.0-stable`. Không đổi code audio/XML hoặc UI. Rà soát pre-release từ `fc6ead9` đạt Release build `0 warning/0 error`, `223/223` test, format/diff sạch, policy Premiere 24/30 `8/8`, policy multi-boundary, publish self-contained `411` file, dependency scan sạch và GitHub artifact API `0`. Release phải đi qua PR/CI, tag annotated `v0.1.0` trên post-merge main và gói installer ký nội bộ local với `privateKeyIncluded=false`; GitHub không nhận binary hoặc private artifact.
