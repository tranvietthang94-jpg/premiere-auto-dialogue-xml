# Phase 06 — pilot Premiere và Windows sạch

Báo cáo bằng chứng hiện tại: [PHASE06_PILOT_RESULT.md](PHASE06_PILOT_RESULT.md).

## Nguyên tắc

Phase 06 chỉ chấp nhận bằng chứng tạo từ đúng ZIP/audit/XML pilot. Không suy diễn từ unit test, XML structure hoặc Phase 00 sang toàn bộ HGE2. Installer chỉ được tạo sau khi các cổng bắt buộc đạt.

Toàn bộ media, ảnh chụp, Premiere project, Translation Results, PCM export và report có đường dẫn riêng tư nằm trong `private-artifacts/phase06-pilot/` và không commit. Chỉ báo cáo đã làm sạch, script validator và fixture tổng hợp mới được đưa vào Git.

## A. Chạy app pilot

1. Giải nén ZIP self-contained Phase 05 vào thư mục mới.
2. Ghi SHA-256 ZIP, phiên bản Windows, trạng thái Internet, và máy có/không có .NET/Python cài sẵn.
3. Mở `PremiereAutoDialogueXml.exe`; xác nhận không tải thêm thành phần và không có telemetry/network request.
4. Chọn XML HGE2 cùng thư mục output pilot mới; chạy **Kiểm tra XML và media**.
5. Xác nhận sequence 25 fps, stereo master, 7 track/7 WAV và 7 warning metadata 16/24-bit dự kiến; không có error.
6. Chạy **Phân tích và xuất kết quả**, ghi elapsed time/peak RAM và giữ XML/audit output.
7. So SHA-256 XML nguồn trước/sau; phải giống nhau. So SHA-256 XML output thật với audit; phải giống nhau.

### Cổng dừng an toàn

Chạy một lượt riêng, bấm **Dừng an toàn** trong khi đang phân tích rồi đóng app:

- worker phải kết thúc trước khi cửa sổ đóng;
- không có XML/audit cuối, `.tmp` hoặc thư mục run rỗng mới;
- XML/WAV nguồn không đổi;
- cancellation không tạo diagnostic lỗi.

## B. Import Premiere Pro

1. Tạo project pilot riêng; không dùng hoặc save đè project show.
2. Import `<sequence>_AutoAudio.xml` do app vừa tạo.
3. Lưu toàn bộ nội dung/ảnh **FCP Translation Results**.
4. Xác nhận sequence `<tên gốc> - AUTO AUDIO` mở được, video còn nguyên, duration `53.760` frame (`00:35:50:10` ở 25 fps), đủ 7 audio track và media tự link đúng file.
5. Kiểm tra các loại fragment:
   - speech/ambiguous vẫn Enabled;
   - noise/bleed đã xác nhận bị Disable nhưng còn clip để bật lại;
   - marker **Cần kiểm tra** và **Gain đã giới hạn +18 dB** nằm đúng vùng;
   - source trim đầu/cuối và ranh giới fragment không tạo gap/overlap nghe thấy.
6. Bật/tắt vài fragment Disable và kiểm tra editor có thể phục hồi audio gốc.

Mất video/audio, media path đổi, duration/track count sai, Translation Results báo lỗi mất audio, hoặc Premiere crash là lỗi chặn; không tạo installer.

## C. Peak sau Premiere

- Export PCM 48 kHz, không normalization/limiter/effect bổ sung.
- Với phrase không bị cap, sample peak sau round-trip phải `-6.0 ± 0.1 dBFS` theo routing đã xác nhận.
- Phrase bị cap phải khớp predicted peak từ audit, không được báo là đạt `-6 dBFS` nếu source quá nhỏ.
- Ghi riêng track/range/phrase ID và checksum WAV export. Không dùng waveform display hoặc hộp Audio Gain làm bằng chứng thay PCM.
- Kết quả này không phải LUFS, true peak, limiter, Master-bus hoặc delivery-ceiling guarantee.

### Hợp đồng stem PCM pilot

Để phép đo ánh xạ được từng phrase trong audit, mỗi WAV pilot phải được export từ đầu sequence `00:00:00:00`, đủ chiều dài, chỉ Solo đúng một track A1–A7 và Mute các track còn lại. Dùng WAV mono integer PCM 48 kHz 16/24/32-bit; ưu tiên 24-bit. Giữ Mix ở `0.0 dB`, không thêm normalization, limiter, track effect hoặc Master effect. Nếu Premiere không cho xuất mono trực tiếp thì dừng và ghi nhận routing thay vì tự convert WAV sau export.

Validator đọc streaming các fragment `speech` và `ambiguous-near-speech` Enabled mang cùng phrase, đo sample peak của toàn phrase và kiểm tra riêng hai điều: Premiere có áp đúng gain/timing hay không, và phrase không cap có thực sự đạt `-6,0 ± 0,1 dBFS` hay không. Report mới luôn chứa SHA-256 audit/WAV và không ghi đè file đã có:

```powershell
.\.tools\dotnet\dotnet.exe run --project tools\PremiereAutoDialogueXml.VerifyPcm -- `
  <audit.json> <track-number> <full-sequence-mono-48k.wav> <new-report.json> [source.xml]
```

Truyền đúng `source.xml` có SHA-256 khớp audit để validator đo lại peak trên source range đã làm tròn theo fragment XML. Cách này tách được sai lệch gain/timing thật khỏi chênh lệch giữa lõi speech và biên video frame.

Chạy A1 trước để xác nhận routing. Chỉ sau khi report A1 hợp lệ mới lặp A2–A7; không dùng một bản full mix để thay cho stem vì nhiều mic cộng lại sẽ làm sai peak từng phrase.

Audit schema `1.3` phải ghi profile `mono-center-equal-power-to-stereo`, `premiereCenterPanCompensationDb=3,0102999566`, policy `max-direct-speech-and-frame-aligned-enabled-phrase-peak` và `preserveVadNegativeHighEnergyConflicts=true`. Validator trừ đúng hệ số routing khỏi predicted peak hậu routing; source-linked gain delta phải nằm trong tolerance, toàn bộ phrase không cap phải đạt `-6,0 ±0,1 dBFS`, còn phrase cap phải khớp predicted peak và không nóng hơn target. Audit `1.0` không có bù, `1.1` chưa có frame-safe gain reference và `1.2` chưa có lớp bảo vệ xung đột VAD/năng lượng, nên không được dùng để đóng cổng của candidate hiện tại.

## D. Nhãn HGE2

Tập nhãn pilot phải có `track`, timeline `start/end`, loại `direct-speech`, `clear-noise`, `clear-bleed` hoặc `ambiguous`, và người xác nhận. Từ audit/XML output tính:

- 100% thời lượng direct speech đã gắn nhãn được giữ Enabled;
- ít nhất 90% thời lượng clear noise/bleed bị Disable;
- 0% vùng ambiguous bị Disable.

Không có nhãn thì chỉ được báo số lượng VAD/fragment/marker, không được tuyên bố đạt ba tỷ lệ trên.

Người nghe phải nghe Context để định vị nhưng gắn nhãn riêng đúng khoảng Mục tiêu. Nhãn chỉ mô tả toàn Context không được đưa vào mẫu số nghiệm thu. Ghi chú có timecode vẫn được dùng làm bằng chứng điều tra ranh giới hoặc VAD, nhưng mọi suy luận phải được ghi riêng và không âm thầm đổi thành nhãn Mục tiêu.

## E. Máy Windows sạch

Chạy ZIP trên Windows 10 x64 và Windows 11 x64 sạch:

- không cài .NET, Python hoặc model ngoài gói;
- ngắt Internet trước lần mở đầu;
- app mở, kiểm tra fixture, bắt đầu/dừng và xuất được;
- không tạo installer hoặc release public trước khi cả hai môi trường đạt.

## Quyết định cuối

Report chỉ có ba trạng thái:

- `passed`: mọi cổng bắt buộc có bằng chứng.
- `failed`: có sai lệch Premiere/audio/safety; dừng phát hành.
- `incomplete`: thiếu import, PCM, nhãn hoặc máy sạch; không được đổi thành passed bằng suy luận.
