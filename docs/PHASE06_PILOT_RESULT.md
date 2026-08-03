# Phase 06 — báo cáo pilot HGE2

Trạng thái: `incomplete`; XML pilot cũ failed PCM, candidate phương án B đang chờ Premiere round-trip, không phát hành.

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

Bằng chứng này xác nhận smoke test import thực tế đã thành công ở mức quan sát của người vận hành.

Ba ảnh timeline Premiere được nhận và giữ ngoài Git. SHA-256 lần lượt là `3819B974ADC9861B54BE11F9CE27E7F0A9EC8D7868DE0F303AD91F8768D78883`, `DD27A869FE69154F55B868425579230DDD31FD1F77B3B789201E9B4610808C59` và `05012255577E6CB9C20AEA5B62FEF5375E0F908DEFFD99C1CB299DA35BC4CD4E`. Ảnh cho thấy:

- sequence `- AUTO AUDIO` đã mở được;
- đủ track A1–A7, clip có tên nguồn và waveform, không thấy chỉ báo media offline trong vùng ảnh;
- các fragment, badge `fx` và marker đã xuất hiện trên timeline;
- timeline overview phủ đến cuối chương trình dự kiến.

Đối chiếu cấu trúc XML nguồn/kết quả xác nhận cùng duration 53.760 frame ở 25 fps (`00:35:50:10`), cùng 3 video track, 7 audio track và cùng tập 7 media reference. Cả XML nguồn và kết quả đều có 0 video clipitem, vì vậy V1–V3 trống trong ảnh là trạng thái của fixture, không phải video bị writer xóa. Audio thay đổi từ 7 clip nguồn thành 8.058 fragment theo thiết kế.

Ảnh không chứa cửa sổ **FCP Translation Results** và không thể chứng minh sample peak, trạng thái Enabled/Disabled của từng fragment hay độ chính xác nhận diện.

## PCM round-trip A1

Người vận hành đã export một stem A1 đủ sequence từ Premiere. Validator xác nhận WAV là mono integer PCM 48 kHz/24-bit, dài đúng 103.219.200 sample (`00:35:50:10` ở 25 fps). SHA-256 WAV là `B38CF1F4188B79C9C2302EA42F2E44DDE1A5BCF10BAC2204E8FB7279C7E63167`.

Theo tiêu chí nghiêm ngặt hiện tại, 0/252 phrase nằm trong `±0,1 dB` quanh peak kỳ vọng nên cổng PCM không đạt. Median sai lệch là `-3,010304 dB`; 237/252 phrase (94,0%) nằm trong `±0,1 dB` quanh chính offset này, gồm 106/112 phrase không cap và 131/140 phrase bị cap. Mười lăm chênh lệch ban đầu xuất hiện vì peak audit đo lõi speech còn XML phải áp gain trên fragment đã làm tròn theo video frame.

Validator .NET chạy lại với XML/media nguồn có SHA-256 khớp audit và đo đúng source range của từng fragment: 252/252 phrase cùng theo offset `-3,010304 dB`, không còn outlier; biên độ sai lệch giữa các phrase nhỏ hơn `0,00005 dB`. Kết quả này xác nhận gain và timing A1 qua XML/Premiere nhất quán, đồng thời cho thấy offset là biến đổi routing chung chứ không phải lỗi gain ngẫu nhiên.

Offset gần `-3,0103 dB` phù hợp với center-pan/downmix mono của Premiere, nhưng target hậu routing của XML pilot cũ chưa đạt `-6 dBFS`. Lượt cũ không được âm thầm nới tolerance hoặc đổi trạng thái thành passed. Report source-linked được giữ ngoài Git; SHA-256 report là `0FF41BDE11413440160BB61AEECBEA97743F6335AE97B9DA604C28DD36C302D8`.

## Quyết định phương án B

Ngày 2026-08-03, người vận hành chọn target gần `-6 dBFS` sau routing Premiere. Bản sửa phải cộng bù `+3,0102999566 dB` vào gain yêu cầu, giữ nguyên trần boost tổng `+18 dB`, ghi profile/hệ số vào audit schema `1.1` và tạo XML pilot mới. XML/WAV/audit pilot cũ vẫn bất biến và chỉ còn giá trị làm bằng chứng trước sửa.

Candidate phương án B đã được tạo từ đúng `test HGE2.xml` trong một run mới. SHA-256 input vẫn là `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`; SHA-256 XML candidate và giá trị audit đều là `416B21677C699003DC324197211079C71FAC12FA260C2A655D173A0907F52BC3`.

Audit schema `1.1` ghi profile `mono-center-equal-power-to-stereo`, compensation `3,010299956639812 dB`, target hậu routing `-6 dBFS` và boost tối đa `+18 dB`. Candidate vẫn có 8.058 fragment/2.132 phrase; số phrase chạm cap tăng từ 882 lên 1.208 và marker tăng từ 2.956 lên 3.282. Mẫu phrase không cap đều có predicted post-routing peak `-6 dBFS`. Đây mới là dự đoán/audit; chưa thay thế bằng chứng PCM từ Premiere.

Gói kiểm thử phương án B đã được publish dạng ZIP self-contained `win-x64`, chưa tạo installer. Gói có 410 file payload, không chứa Python hoặc PDB; kích thước ZIP là 71.101.669 byte và SHA-256 là `70CE44E1A1D5C7139C1964E6CED77361F3B58485E4573E3B4F00C8AB86589B71`. Manifest ghi model `6.2.1` với SHA-256 `1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3`.

## Các cổng còn thiếu

- Lưu **FCP Translation Results** nếu Premiere có tạo và xác nhận không có lỗi làm mất audio.
- Kiểm tra vài fragment speech/ambiguous/noise, marker và khả năng bật lại clip Disable.
- Import XML phương án B mới, export lại A1 và yêu cầu source-linked PCM report đạt `-6.0 ± 0.1 dBFS` cho phrase không cap; phrase cap phải khớp predicted peak hậu routing.
- Gắn nhãn đủ để tính 100% direct speech được giữ, ít nhất 90% clear noise/bleed bị Disable và 0% ambiguous bị Disable.
- Chạy cổng **Dừng an toàn** và xác nhận không để XML/audit/tmp dở dang.
- Chạy ZIP offline trên Windows 10 x64 và Windows 11 x64 sạch, không có .NET/Python cài sẵn.

Không tạo installer và không merge Phase 06 cho đến khi các cổng bắt buộc có đủ bằng chứng.
