# Phase 13 — Độ tin cậy CI và headroom full HGE

Trạng thái: `đạt`. Implementation đã merge qua PR `#18` ngày 2026-08-22 tại commit `ad89d2a38488ecfb05d9f41ad18085d9359f494b`; PR CI `32562437223` và hậu merge CI `32562657372` đạt toàn bộ quality gate. Closeout ngày 2026-08-23 chuyển sang chính sách GitHub chỉ lưu source code; installer dùng thật chỉ giữ cục bộ.

## Quyết định sản phẩm

Phase 13 chỉ trả nợ kỹ thuật sau Phase 12. Phase không thêm preview, playback hoặc editor; không mở thêm frame rate, source stereo, sample rate hay routing. XML/WAV/`.prproj` nguồn tiếp tục bất biến và output audio/XML phải giữ đúng semantic Phase 12.

## Mục tiêu

1. Đưa policy validator Premiere 24/30 fps vào CI: strict mode phải từ chối thay đổi sequence depth; explicit mode chỉ được phép `24 → 16` và vẫn từ chối mọi thay đổi Enabled, timing, gain, media hoặc marker.
2. Chấm dứt phụ thuộc vào quota GitHub Actions artifact: workflow không upload hoặc quản lý installer; CI chỉ build/smoke-test bản tạm trên runner, còn installer dùng thật được tạo và giữ cục bộ ngoài Git.
3. Tạo headroom thật cho full HGE bằng profile và tối ưu phần quét/diagnostic provenance; không đổi VAD, noise/bleed, phrase, gain, routing, marker hoặc XML encoding.

## Baseline khóa

- Release `198/198` test; build/format/diff sạch.
- HGE2 Phase 12: `76,7 giây`, peak working set `320,4 MB`.
- Full HGE Phase 12: `77 phút 02 giây`, peak working set `1.280,5 MB`, XML `204,34 MB`, audit `677,37 MB`.
- Full HGE giữ coverage mismatch `0`, lost Enabled `0`, newly Enabled `0`; M19/A3 frame `11214–11218` Enabled.
- XML baseline full HGE SHA-256 `13573F508CA95E12EDB1D10B19322AE63B3D06ABFAF87AA24DCB32C2B98DE70D`.

## Slice 13A — validator policy trong CI

- Fixture tổng hợp nhỏ, không dùng media hoặc XML riêng tư.
- Chạy cả 24 và 30 fps.
- Chứng minh strict mode fail với `sequence depth 24 → 16`.
- Chứng minh explicit mode pass và chỉ ghi đúng normalization `sequence-depth-24-to-16`.
- Chứng minh explicit mode vẫn fail với depth transition khác và một thay đổi semantic đại diện.

## Slice 13B — source-only và installer cục bộ

- Liệt kê chính xác và dọn artifact repo cũ trước khi đổi policy.
- Giữ bản phát hành nội bộ đã ký và installer kỹ thuật ở local; không commit, upload Actions artifact hoặc tạo GitHub Release chứa binary.
- Workflow không cần quyền `actions: write`; build/publish/installer smoke vẫn là quality gate bắt buộc nhưng output chỉ tồn tại tạm trên runner.
- `private-artifacts/`, `artifacts/`, installer, ZIP và chứng thư tiếp tục bị `.gitignore` chặn.

## Slice 13C — performance và audit bounded

- Quét PCM một lần cho hai front-end VAD thay vì đọc/giải mã cùng source hai lần.
- Sau khi provenance đã bounded, long timeline thử chạy hai track song song. Lượt full HGE đầu vượt cổng ở peak `1.601,5 MB`, nên cấu hình này bị loại. Policy staged hai worker cho bốn track đầu rồi một worker đạt `61 phút 29 giây` và peak `1.430,4 MB`, nhưng peak vẫn cao hơn baseline `1.280,5 MB`; candidate staged chỉ là bằng chứng profile, không được adopt. Policy production cuối giữ một worker cho toàn bộ long timeline để lấy lại headroom RAM.
- Regression chứng minh kết quả paired scan giống hai lượt scan độc lập tại sample/timeline/RMS/peak/probability.
- Diagnostic comparison lớn chỉ giữ count, SHA-256 toàn stream và mẫu chẩn đoán bounded; không giữ hàng trăm nghìn record lặp trong audit cuối.
- Audit schema tăng phiên bản; validator kiểm count/hash/sample và fail-safe `baseline Enabled → final Disabled = 0` trước publication.

## Cổng nghiệm thu

- CI chạy validator policy trước publish/installer và phát hiện được policy bị nới sai.
- GitHub chỉ lưu source code; workflow không upload installer artifact và artifact API của repo ở `0` theo thiết kế.
- HGE2 không được chậm hơn baseline quá `5%`, đồng thời phải giảm peak RAM và audit bytes; full HGE phải thấp hơn baseline về runtime, peak RAM và audit bytes.
- Comparator với Phase 12: source hash khớp, coverage mismatch `0`, lost Enabled `0`, newly Enabled `0`, temp file `0`; M19 vẫn Enabled.
- XML output giữ semantic audio Phase 12; thay đổi audit diagnostic không được làm thay đổi Enabled, gain, timing hoặc marker.
- Release/build/format/publish/installer smoke và CI đều đạt trước merge.

## Kết quả local

### Validator 24/30 và CI

- `scripts/phase13/Test-PremiereRoundTripPolicy.ps1` chạy `8/8` case tổng hợp, không dùng XML/media riêng tư.
- Ở cả 24 và 30 fps, strict mode từ chối sequence depth `24 → 16`; explicit mode chỉ chấp nhận đúng `sequence-depth-24-to-16`.
- Explicit mode vẫn từ chối depth `24 → 32` và thay đổi `enabled`; validator không bị biến thành công tắc bỏ qua semantic.
- Workflow chạy policy test sau unit test và trước publish/installer; failure vì policy sẽ dừng quality gate.

### Artifact quota

- Trước cleanup có `52` installer artifact, tổng `2.699.478.351 bytes` xấp xỉ `2,7 GB`.
- Đã xóa an toàn `51` bản cũ/trùng, giải phóng `2.647.547.749 bytes`; fallback `51.930.602 bytes` còn lại sau đó cũng được workflow cleanup xóa trước retry.
- Policy thử nghiệm ban đầu cho main/manual xóa installer cũ rồi upload một bản retention `7 ngày`; build/publish/installer smoke vẫn độc lập với bản sao best-effort.
- Quota được xác định là account-wide: đã xóa thêm `29` artifact `github-pages` hết hạn của repository `filmtechxai`, giải phóng `953.430.813 bytes`. Theo yêu cầu chủ dự án, repository đó được xóa hoàn toàn ngày 2026-08-23 sau khi source local được đồng bộ và tạo Git bundle đầy đủ; việc này còn loại bỏ artifact Pages `50.154.296 bytes` và cache `147.371.032 bytes` khỏi GitHub.
- Main/retry `32562657372`, `32566303057`, `32616557631`, `32616802621`, `32618600568`, `32638141440`, `32641055228` và `32644110187` đều đạt build, `201/201` test, policy `8/8`, publish và installer smoke; upload vẫn nhận `Failed to CreateArtifact`.
- GitHub Billing xác nhận gói Free đã dùng `0,5/0,5 GB` Actions storage của kỳ, nên xóa file không giảm usage đã tích lũy trong kỳ. Chủ dự án quyết định không mua thêm quota và không chờ reset: từ 2026-08-23 GitHub chỉ lưu source code.
- Draft RC1 GitHub chứa `9` asset đã được xóa sau khi đối chiếu đủ tên, kích thước và SHA-256/digest với bản phục hồi cục bộ trong `private-artifacts/release-v0.1.0-rc1`; không xóa bản cục bộ hoặc source.
- Workflow cuối bỏ `actions: write`, bước cleanup và `actions/upload-artifact`; CI vẫn chứng minh installer build/cài/mở/xác minh/gỡ được nhưng binary tạm bị hủy cùng runner. Artifact API `0` là trạng thái mong muốn, không còn là blocker.
- Source-only PR `#19` CI `32645714062` đạt restore/build, `201/201` test, policy `8/8`, self-contained publish, xác minh Inno Setup và installer smoke trong `3 phút 06 giây`; không có bước hoặc annotation upload artifact.

### HGE2

- Candidate `phase13-hge2-pilot-20260822-2`: `78,567 giây`, peak `276,2 MB`, XML `10,32 MB`, audit `9,45 MB`.
- So baseline Phase 12: runtime `+2,4%` và nằm trong giới hạn `+5%`; peak RAM giảm `13,8%`; audit giảm `65,2%`.
- Comparator đạt: source hash khớp, coverage mismatch `0`, lost Enabled `0`, newly Enabled `0`, temp `0`, `8.291` fragment.
- M19/A3 frame `11214–11218` là đúng một fragment `Ambiguous`, `enabled=true`, unity gain.

### Full HGE

- Candidate cuối `phase13-full-hge-pilot-20260822-3`: `75 phút 52,4 giây`, peak `1.218,3 MB`, XML `204,34 MB`, audit `157,52 MB`, `162.922` fragment.
- So baseline Phase 12: nhanh hơn `1 phút 09,6 giây` (`1,5%`), peak RAM giảm `62,2 MB` (`4,9%`), audit giảm khoảng `519,85 MB` (`76,7%`).
- Comparator đạt: source hash khớp, output hash nội bộ khớp, coverage mismatch `0`, lost Enabled `0`, newly Enabled `0`, temp `0`; toàn bộ transition chỉ là `speech → speech`, `noise → noise`, `ambiguous → ambiguous`.
- XML giữ nguyên kích thước và semantic audio; hash mới `7479DEE5E1BEB7B18E0007CB97A7275FA0AFDC1E91915CE1E5D190396A98A0EF` vì audit/provenance và UUID output mới, không phải đổi quyết định audio.
- Audit schema `1.9`, SHA-256 `D544015B8470AEF8D14C2C31566DBC20C51BBCC9199920B67803638073184882`; full count/hash vẫn được giữ trong khi record chẩn đoán bounded.

Hai thử nghiệm tăng worker không được adopt: hai worker toàn phần vượt peak `1.601,5 MB`; staged `2 → 1` đạt `61 phút 29 giây` nhưng peak `1.430,4 MB`, cao hơn baseline. Production giữ một worker cho long timeline để cải thiện đồng thời runtime, RAM và audit bytes.

### Quality gate local cuối

- Format verify và `git diff --check` sạch; Release build `0 warning/0 error`.
- Unit/regression `201/201` đạt; policy validator `8/8` đạt.
- Publish self-contained có `410` payload file.
- Installer unsigned SHA-256 `318C1120064767C269099162D14C89C9AB152C9D61F8D3ADAC0B3B155A0C4B51`; smoke test đạt cài exit `0`, mở app, payload `410/410`, gỡ exit `0`, xóa payload/registry và giữ user-created sentinel.
- Installer này chỉ là quality-gate artifact, không thay RC1 đã ký và không được phát hành.

## Điều kiện rollback

- Nếu paired scan làm lệch bất kỳ observation/VAD probability nào, rollback tối ưu scan.
- Nếu compaction làm mất aggregate safety, hash provenance hoặc khiến validator không còn chứng minh fail-safe, rollback compaction.
- Nếu HGE có lost Enabled, newly Enabled, gain/timing drift hoặc M19 regression, không adopt thay đổi performance dù runtime tốt hơn.
