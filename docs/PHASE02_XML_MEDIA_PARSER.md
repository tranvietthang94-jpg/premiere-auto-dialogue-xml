# Phase 02 — parser XML và media WAV

## Phạm vi đã triển khai

Phase 02 chỉ đọc XML và header WAV. Không quét sample audio, không tạo output và không sửa XML/WAV/project nguồn.

- Chấp nhận đúng một sequence 25 fps, master stereo 48 kHz và các track nghệ sĩ Premiere Stereo chứa source mono.
- Giữ source trim, timeline gap, track lệch đầu/cuối và nhiều WAV nối tiếp trên cùng track.
- Giải mã đúng `file://localhost/F%3A/...` do Premiere xuất, gồm đường dẫn Unicode và dấu cách.
- Dùng `pproTicksIn/Out` làm biên source chính xác; `in/out` theo frame được dùng làm fallback có cảnh báo.
- Chấp nhận sai lệch làm tròn tối đa một frame giữa trường frame và `pproTicks`; sai lệch lớn hơn vẫn bị xem là retime.
- Đọc header RIFF/WAVE theo kiểu streaming với offset 64-bit; không cấp phát theo kích thước chunk `data`.
- Hỗ trợ PCM mono 48 kHz 16/24/32-bit và WAVE_FORMAT_EXTENSIBLE PCM.
- Header WAV thật quyết định sample rate, channel và bit depth. Metadata XML khác header chỉ tạo cảnh báo khi source range vẫn hợp lệ.
- Chỉ giữ các PCM frame hoàn chỉnh; phần đuôi 1–3 byte chưa đủ một frame được cảnh báo, thiếu từ một frame trở lên bị từ chối.

## Điều kiện từ chối trước phân tích

- Không đúng một sequence, nested sequence, multicam hoặc generator.
- Effect/filter, transition, keyframe automation, link hoặc dấu hiệu retime.
- Master không stereo 48 kHz, track submix/không phải Premiere Stereo, routing ngoài output 1/2.
- Clip source không mono track 1, clip/track đã Disable hoặc track bị khóa.
- Overlap trên cùng track, ID clip trùng, source range nằm ngoài WAV.
- Media thiếu, không phải WAV hoặc WAV ngoài hợp đồng PCM của MVP.
- DOCTYPE khác nguyên văn `<!DOCTYPE xmeml>`, internal/external DTD, XML sai cấu trúc hoặc lớn hơn 128 MB.

## Bằng chứng kiểm thử

Ngày 2026-08-02 trên Windows:

- Release build: đạt, `0` warning và `0` error.
- MSTest: `28/28` đạt sau khi thêm fixture synthetic cho Unicode path, source trim, gap, overlap, duplicate ID, nested sequence, external DTD, effect, metadata mismatch, source ngoài media, PCM 24-bit, WAVE_FORMAT_EXTENSIBLE và stream logic gần 4 GB.
- `test HGE2.xml`: đạt; 7 track, 7 clip và 7 WAV. Có 7 cảnh báo XML khai 16-bit trong khi header WAV thật là 24-bit.
- `test HGE.xml`: đạt; 7 track, 19 clip và 19 WAV. Có 27 cảnh báo được giữ minh bạch: 19 metadata bit depth, 5 pproTicks làm tròn dưới một frame, 2 source/timeline làm tròn một frame và 1 byte đuôi PCM chưa đủ frame.
- Không output mới được tạo khi chạy kiểm tra hai XML thật.

## Công cụ chẩn đoán nội bộ

`PremiereAutoDialogueXml.Inspect` chạy cùng parser Core và chỉ in JSON tóm tắt đã làm sạch; không ghi đường dẫn media hoặc file kết quả:

```powershell
dotnet run --project tools/PremiereAutoDialogueXml.Inspect -- "F:\demo\test HGE2.xml"
```

Công cụ này phục vụ kiểm thử phát triển, không nằm trong gói cài đặt dành cho người dùng.
