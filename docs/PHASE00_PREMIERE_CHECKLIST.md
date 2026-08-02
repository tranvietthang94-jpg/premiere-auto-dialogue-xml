# Phase 00 — Premiere XML compatibility gate

Phase này phải đạt trước khi xây engine. Mọi thao tác Premiere dùng project/sequence thử riêng; không lưu đè project đang dựng.

## Tạo input cục bộ

```powershell
powershell -ExecutionPolicy Bypass -File scripts\phase00\New-CompatibilityFixture.ps1
```

Script tạo WAV tone và XML materialized trong `private-artifacts/phase00/`, thư mục đã bị Git bỏ qua.

## Round-trip bắt buộc

1. Import `phase00-compatibility-input.xml` vào Premiere Pro 2026.
2. Xác nhận sequence dài 10 giây có năm đoạn, đoạn cuối Disabled.
3. Export sequence thành Final Cut Pro XML và giữ FCP Translation Results.
4. Export audio toàn sequence thành WAV PCM 48 kHz, không normalize, không effect bổ sung.
5. Chạy validator:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\phase00\Test-CompatibilityExport.ps1 `
  -ExportedXml "<premiere-export.xml>" `
  -RenderedWav "<premiere-render.wav>"
```

## Điều kiện đạt

- Năm fragment và source trim giữ nguyên.
- Trạng thái Disabled giữ nguyên.
- Audio Levels `+6 dB`, `+12 dB` và hai mức `+9 dB` được giữ trong XML export.
- Gain tương đối sau render lần lượt là `0`, `+6`, `+12`, `+18 dB`, sai số tối đa `0.1 dB`.
- Segment Disabled không có tín hiệu trên `-90 dBFS`.

Nếu bất kỳ điều kiện nào không đạt, không tiếp tục Phase 01 và không âm thầm đổi sang render media hoặc cap gain thấp hơn.

Kết quả chạy trên Premiere Pro 2026 được ghi tại [PHASE00_RESULT.md](PHASE00_RESULT.md): cổng không đạt vì hai Audio Levels `+9 +9 dB` chỉ còn `+9 dB` sau round-trip.
