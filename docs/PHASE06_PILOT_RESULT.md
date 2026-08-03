# Phase 06 — báo cáo pilot HGE2

Trạng thái: `incomplete`

Ngày ghi nhận: 2026-08-03.

Báo cáo này chỉ chứa bằng chứng đã làm sạch. XML, WAV, project Premiere, ảnh chụp và audit đầy đủ được giữ ngoài Git trong `private-artifacts/phase06-pilot/`.

## Gói ứng dụng đã dùng

- Loại gói: ZIP self-contained `win-x64`, chưa phải installer.
- SHA-256 ZIP: `DCD2EC940873DF6A6811EC1981D7AFEFD2E7321A0CC5E2BFFF32BF9C4BC5D837`.
- Gói có app, .NET runtime, ONNX Runtime, model và thông báo giấy phép; không cần Python.
- CI Windows của Phase 06 đã restore, build, test và kiểm tra publish thành công: [run 30787288756](https://github.com/tranvietthang94-jpg/premiere-auto-dialogue-xml/actions/runs/30787288756).

## Bằng chứng tự động từ lượt chạy

- Fixture: `test HGE2.xml`.
- SHA-256 XML nguồn trước và sau lượt chạy đều là `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`; app không sửa input.
- SHA-256 XML kết quả thực tế và giá trị ghi trong audit đều là `887F8F90AAA456C229D646CE9ED53F5A20104406068DB4029820BDC92945D729`.
- Model VAD: `6.2.1`; SHA-256 `1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3`.
- XML kết quả có 8.058 fragment và 2.956 marker.
- Có 3.196 fragment `speech`, 2.788 `noise`, 0 `bleed` và 2.074 `ambiguous`.
- Toàn bộ 2.788 fragment bị Disable đều có trạng thái `noise`.
- Toàn bộ 2.074 fragment `ambiguous` vẫn Enabled.
- Có 2.132 cụm lời trong audit; 882 cụm chạm giới hạn boost `+18 dB`. Đây là số cụm theo quyết định phân tích, không phải bằng chứng rằng mọi cụm đã được người nghe gắn nhãn đúng.

## Bằng chứng từ người vận hành

Ngày 2026-08-03, người vận hành xác nhận đã tự import XML kết quả vào Premiere Pro trong một lượt kiểm tra và đánh giá kết quả ban đầu là tốt.

Bằng chứng này xác nhận smoke test import thực tế đã thành công ở mức quan sát của người vận hành. Hiện chưa lưu nội dung **FCP Translation Results**, ảnh timeline, PCM export hoặc checklist chi tiết nên chưa dùng bằng chứng này để khẳng định peak, duration, track count, media link hay độ chính xác nhận diện.

## Các cổng còn thiếu

- Lưu **FCP Translation Results** và xác nhận video, duration, đủ 7 audio track, media tự link, không mất audio.
- Kiểm tra vài fragment speech/ambiguous/noise, marker và khả năng bật lại clip Disable.
- Export PCM không normalization/effect để xác minh phrase không bị cap đạt `-6.0 ± 0.1 dBFS` và phrase bị cap khớp predicted peak.
- Gắn nhãn đủ để tính 100% direct speech được giữ, ít nhất 90% clear noise/bleed bị Disable và 0% ambiguous bị Disable.
- Chạy cổng **Dừng an toàn** và xác nhận không để XML/audit/tmp dở dang.
- Chạy ZIP offline trên Windows 10 x64 và Windows 11 x64 sạch, không có .NET/Python cài sẵn.

Không tạo installer và không merge Phase 06 cho đến khi các cổng bắt buộc có đủ bằng chứng.
