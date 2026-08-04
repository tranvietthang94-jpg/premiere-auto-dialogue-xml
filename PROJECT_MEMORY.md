# Memory handoff — Premiere Auto Dialogue XML, Phase 00–07

Ngày chốt: 2026-08-04 (Asia/Saigon). Đây là memory source-of-truth để một phiên Codex mới tiếp tục nâng cấp dự án mà không làm lại các phase đã hoàn tất.

## Định hướng và vị trí dự án

- Workspace thật: `F:\RIN APP\App-Auto-Edit_codex_2`.
- Private GitHub repo: `tranvietthang94-jpg/premiere-auto-dialogue-xml`.
- Nhánh hiện tại: `main`, sạch; HEAD/merge commit Phase 07: `8bdf47d0dc3a3eb17a143a3145b5cf59369d9b9b`.
- PR Phase 07: `#8`, đã merge bằng merge commit. Các phase 00–07 đều hoàn tất; khi nâng cấp hãy bắt đầu phase mới từ `main`, không restart/re-audit toàn dự án.
- Private draft prerelease: tag `v0.1.0-rc.1`, tên `Premiere Auto Dialogue XML 0.1.0 RC1`, target `8bdf47d`; URL draft hiện tại `https://github.com/tranvietthang94-jpg/premiere-auto-dialogue-xml/releases/tag/untagged-1c4ff86dc76dc88c43e3`.
- Tài liệu phase đầy đủ nằm trong `docs/`; đọc trước `README.md`, `docs/IMPLEMENTATION_PLAN.md`, `docs/PHASE00_RESULT.md`, `docs/PHASE06_PILOT_RESULT.md`, `docs/PHASE07_INSTALLER_RELEASE.md`, `docs/INTERNAL_CODE_SIGNING.md`.

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

## Lỗi đã gặp và cách tránh

1. Gain XML: không stack hai Audio Levels cùng loại và không ghi literal dB vào Gain filter. Dùng encoding Phase 00.
2. Routing -3.0103 dB: target phải là post-routing Option B; không bỏ compensation hoặc tăng boost cap.
3. Frame rounding: đo chỉ lõi speech làm một phrase nóng; giữ policy frame-safe max core + Enabled phrase frames.
4. VAD false negative M19: giữ conflict VAD thấp/năng lượng cao liên tục thành ambiguous; note thực tế người dùng rất có giá trị dù nhãn Context không dùng làm metric.
5. Missing media trên VM: XML không mang WAV; phải chép đúng media references. Lỗi `media-missing` và nút Analyze disabled là fail-safe đúng.
6. Installer test harness đầu tiên duyệt sai registry path và để lại một key thử nghiệm. Đã xóa sau khi xác minh exact target; harness giờ chỉ dùng AppId ổn định, recovery uninstall và kiểm tra xóa HKCU entry.
7. UI installer ban đầu còn `Browse`, disk-space và `Destination location` tiếng Anh. Đã bổ sung `ButtonWizardBrowse`, `DiskSpace*`, `ReadyMemo*`; trang chính đã kiểm tra trực quan tiếng Việt.
8. GitHub artifact ban đầu lồng thư mục run. Workflow đã copy release files vào staging phẳng trước `actions/upload-artifact@v7`.
9. `Import-Certificate` từng treo khi trust self-signed root, tạo trạng thái dở. Đã xóa đúng cert/output partial và đổi script sang `certutil.exe -user -f -addstore Root/TrustedPublisher`.
10. SignTool không ký app ở payload path quá dài (`File not found`). Fix: ký app bằng `Set-AuthenticodeSignature` trước khi tạo publish manifest/ZIP; Inno SignTool chỉ ký Setup/uninstaller ở path ngắn. Không chuyển việc ký app xuống sau manifest vì sẽ làm hash payload stale.
11. Self-signed không tự tạo SmartScreen reputation và chưa trusted trên máy mới. Không gọi đây là chữ ký công cộng, không tuyên bố “virus-free”. Luôn phát hành checksum/provenance và hướng dẫn trust nội bộ.
12. Inno Setup hiện yêu cầu xem xét commercial license nếu phát hành thương mại; dự án hiện private/internal. Recheck license trước khi thương mại hóa.

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

Phase 00–07 hoàn tất, main/CI xanh, installer signed nội bộ đã hoạt động và signer đã được người dùng xác nhận trên máy đích. Việc hợp lý tiếp theo là một Phase 08 riêng cho yêu cầu nâng cấp mới; không còn công việc bắt buộc trong Phase 07.
