# Phase 06 — báo cáo pilot HGE2

Trạng thái: `incomplete`; candidate audit `1.3` đã giữ đúng M19, đạt PCM round-trip A1–A7, import không hiện cảnh báo Translation Results và clip Disable bật lại nghe được audio gốc. Ngày 2026-08-04, chủ dự án quyết định bỏ cổng nghe nhãn Mục tiêu; vì vậy báo cáo không tuyên bố ba tỷ lệ 100%/90%/0% dựa trên nhãn người nghe. Còn thiếu kiểm thử trên máy Windows sạch; không phát hành.

Ngày ghi nhận mới nhất: 2026-08-04.

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

### PCM của candidate phương án B đầu tiên

Người vận hành import candidate và export lại A1 thành WAV mono integer PCM 48 kHz/24-bit, đủ 103.219.200 sample. SHA-256 WAV là `A44D924636643FEB6257BA965AD50E6E6D1298A4312C43CEF13ED3CF0E5E8451`.

Validator source-linked xác nhận Premiere áp đúng gain/timing cho 252/252 phrase; median delta là `-0,000005 dB` và sai lệch tuyệt đối lớn nhất dưới `0,000023 dB`. Tuy nhiên cổng target nghiêm ngặt chỉ đạt 67/68 phrase không cap: `T01-P000141` render ở `-5,0345 dBFS`, nóng hơn target gần `0,97 dB`. Peak lớn nhất trong các phrase A1 là `-1,0098 dBFS` ở một phrase đã cap. Vì vậy candidate này không được chấp nhận dù nghe thử ban đầu tốt. Report validator schema `1.1` có SHA-256 `1E1CA3E29F181C8051EFF03BAD357379CB2B1BB325AA02BB2FC16F50E5949C49`.

### Candidate frame-safe thay thế

Gain reference được sửa thành peak lớn hơn giữa lõi direct speech và toàn bộ frame `speech`/`ambiguous-near-speech` Enabled của cùng phrase sau khi căn theo frame XML. Audit được nâng lên schema `1.2` và ghi policy `max-direct-speech-and-frame-aligned-enabled-phrase-peak`; validator cũng tách riêng gain conformance với target conformance để không thể báo đạt khi Premiere áp đúng gain nhưng output vẫn lệch `-6 dBFS`.

Candidate mới dùng cùng input SHA-256 `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`; SHA-256 XML và giá trị audit đều là `72EADDE9ED897A32BD11A4EFAD453FE5A77742E73A4BC69E2BEB1248B10D1AB8`, SHA-256 audit là `B570033C633B8D63156DF7E063020703C1FD3EA5187D8914D2A87CFB15167BB0`. Cấu trúc vẫn có 8.058 fragment/2.132 phrase; 1.197 phrase chạm cap, 3.271 marker. Toàn bộ 935 phrase không cap có predicted post-routing peak `-6 dBFS`. Đây vẫn là dự đoán cho candidate mới và cần thêm một PCM round-trip từ Premiere.

Người vận hành đã import candidate frame-safe và export lại A1. WAV là mono integer PCM 48 kHz/24-bit, đủ 103.219.200 sample; SHA-256 `06EFCC2E38C34DBF2ED1875A0BD15EE1A79D998AF42E32670C6492D91CD0CC0B`. Validator source-linked đạt 252/252 phrase: cả 252 phrase khớp gain/timing, 71/71 phrase không cap đạt target, 181 phrase cap đều khớp predicted peak và không phrase nào nóng hơn `-6 dBFS`. Peak của nhóm không cap nằm từ `-6,000015` đến `-5,999978 dBFS`; phrase cap nóng nhất là `-6,225038 dBFS`. Median source-linked delta là `-0,000005 dB`, sai lệch tuyệt đối lớn nhất dưới `0,000023 dB`. Report SHA-256 là `E602E0800CBD914E9744B583A528D760FF73CE59AF4A8D4F19FF3339CB463292`. Cổng PCM A1 đạt.

Người vận hành tiếp tục export A2–A7 từ cùng sequence, mỗi stem đều là mono integer PCM 48 kHz/24-bit và đủ 103.219.200 sample. Cả sáu report đều đạt:

| Track | Phrase | Không cap | Cap | SHA-256 report |
|---|---:|---:|---:|---|
| A1 | 252 | 71 | 181 | `E602E0800CBD914E9744B583A528D760FF73CE59AF4A8D4F19FF3339CB463292` |
| A2 | 286 | 69 | 217 | `F44BE9A3D1E2F53389A0DFA55738644696A89D3D892FC4AC3426C8912FB829AA` |
| A3 | 389 | 143 | 246 | `4A8BFE65810F8C7E9CB2057AD4E1EDC80AAFB458B3DC831CDDFEAFBB92FB587B` |
| A4 | 213 | 95 | 118 | `035598701A30E4D1F91EA6E91BDED0E632EE4ECB84C95470D2CFEFF802F46595` |
| A5 | 360 | 248 | 112 | `3311175FED7DE02FD607C7762884E53906562A20003C56108633D9055D3FE9A4` |
| A6 | 279 | 127 | 152 | `46723D116FA3879EB988FFE960CCF0C994B34C045BC89F2D2DF0A4C0DD1B66E2` |
| A7 | 353 | 182 | 171 | `0B514680C0D26734030DB341D226C3CF910EDCF45F54D133331B009183364E38` |

Tổng hợp A1–A7: 2.132/2.132 phrase đạt gain/timing; 935/935 phrase không cap đạt target; 1.197 phrase cap đều khớp predicted peak và không phrase nào nóng hơn `-6 dBFS`. Peak quan sát của nhóm không cap nằm từ `-6,000019` đến `-5,999974 dBFS`; phrase cap nóng nhất là `-6,004388 dBFS`. Sai lệch source-linked tuyệt đối lớn nhất của toàn bộ bảy track dưới `0,000027 dB`. Cổng PCM HGE2 A1–A7 đạt.

Kết quả trên chỉ đóng cổng PCM cho candidate audit `1.2`. Sau review nội dung, candidate này trở thành bằng chứng lịch sử và không còn là build dự kiến phát hành.

## Review Context và candidate audit 1.3

Người vận hành đã điền đủ 63 dòng nghe mù và viết timecode chi tiết, nhưng chỉ đánh giá toàn khoảng Context thay vì cô lập Mục tiêu. Vì vậy 18 nhãn `L`, 29 nhãn `N` và 16 nhãn `M` không được dùng trực tiếp để tính ba tỷ lệ nghiệm thu.

Các ghi chú vẫn phát hiện `M19` trên A3 có lời tại `00:07:28:15` nằm trong fragment noise Disable. Chẩn đoán đúng XML/media cho thấy VAD cao nhất chỉ `0,07245`, trong khi bốn block liên tiếp 128 ms cao hơn noise floor 11–14 dB và A3 lớn hơn mọi mic khác tại thời điểm đó. App được harden để giữ chuỗi VAD âm/năng lượng cao liên tục ít nhất 120 ms thành `ambiguous`, Enabled và marker `Cần kiểm tra`; transient ngắn hơn vẫn là noise.

Candidate audit `1.3` mới dùng cùng input SHA-256; XML output có SHA-256 `5ADF7F4717E8A06330B6F0A73F6C1AED4FCC444FA63101A7A3BD995A015CEDCD`, audit có SHA-256 `95C8C430861757FA1341A57180D6B3188F4F793C28232CA30F47AB732B20B182`. Candidate có 10.468 fragment, 4.775 marker, 2.132 phrase và 1.885 fragment energy/VAD conflict đều Enabled. `M19` được giữ tại frame `11214–11218`. Runtime khoảng 30,0 giây, peak working set 169,4 MB, input không đổi, 7 warning metadata dự kiến và 0 error.

Ba phrase trên A3/A4/A5 đổi gain từ `0,143–0,291 dB` do frame Enabled mới tham gia phép đo frame-safe; 2.129 phrase còn lại không đổi và toàn bộ 935 phrase không cap vẫn dự đoán đúng `-6 dBFS`. Vì XML/audit đã thay đổi, PCM cũ không được gắn sang candidate `1.3`. Chi tiết: [PHASE06_CONTEXT_LABEL_REVIEW.md](PHASE06_CONTEXT_LABEL_REVIEW.md).

Người vận hành đã import candidate `1.3`, xác nhận M19/A3 tại `00:07:28:15` nghe được và export lại đủ A1–A7. Bảy WAV đều là mono PCM 48 kHz/24-bit, đủ 103.219.200 sample và liên kết đúng audit/source SHA-256. Validator đạt 2.132/2.132 phrase gain/timing, 935/935 phrase không cap ở target, 1.197 phrase cap khớp dự đoán và không phrase cap nào nóng hơn `-6 dBFS`. Peak không cap nằm từ `-6,000019` đến `-5,999974 dBFS`; phrase cap nóng nhất `-6,004388 dBFS`; sai lệch source-linked lớn nhất dưới `0,000027 dB`. Cổng PCM candidate audit `1.3` đạt; checksum từng WAV/report nằm trong báo cáo chi tiết.

Trong cùng lượt kiểm tra, Premiere không hiện cửa sổ **FCP Translation Results**, tức không có cảnh báo dịch XML hiển thị để lưu. Người vận hành bật lại thủ công một clip đang Disable và xác nhận audio gốc nghe bình thường. Cổng phục hồi non-destructive đạt; kết luận không được mở rộng thành một Translation Results report vì Premiere không tạo cửa sổ đó.

## Dừng an toàn trên app thật

App WPF build từ commit Phase 06 được mở trực tiếp, chọn `test HGE2.xml` và một thư mục gate trống riêng. Sau khi kiểm tra XML/media đạt, phân tích được bắt đầu và quan sát thấy nhiều worker đang chạy; người kiểm thử bấm **Dừng an toàn** khi trạng thái đang ở bước phân tích. UI chuyển sang `Đã dừng` và `Không tạo output dở`, sau đó cửa sổ đóng bình thường.

Hậu kiểm xác nhận thư mục gate vẫn có 0 entry: không XML, audit, `.tmp` hoặc thư mục run rỗng; process của build đã kết thúc. SHA-256 XML nguồn sau phép thử vẫn là `09FD290C5CB8401DEF7EA9701433F7BD1799B0300ABA9244A8BF88C022A8C897`. Bộ test Release sau thao tác đạt 90/90. Cổng dừng an toàn đạt.

## Gói frame-safe cũ cho máy sạch

Candidate audit `1.2` đã được publish thành ZIP self-contained `win-x64`, chưa tạo installer. Gói có 410 file payload, không chứa Python hoặc PDB, model `6.2.1` có SHA-256 `1A153A22F4509E292A94E67D6F9B85E8DEB25B4988682B7E174C65279D8788E3`. ZIP dài 71.104.231 byte và có SHA-256 `22E1A3A016D0C078594D8464A695FBF12D3217488368121D01F54D5F752B59E5`. Gói này là bằng chứng lịch sử trước lớp bảo vệ mới và không còn là gói dùng để đóng cổng phát hành.

Máy phát triển hiện tại là Windows 11 Pro x64 và không có Windows Sandbox; vì vậy lượt chạy trên máy này không được dùng thay bằng chứng Windows 10/11 sạch. Gói vẫn chờ thử offline trên hai môi trường sạch, không cài .NET/Python và không có Internet.

## Gói candidate audit 1.3

Build chứa lớp bảo vệ VAD/năng lượng đã được publish thành ZIP self-contained `win-x64`, chưa tạo installer. Gói có 410 payload file, không có Python/PDB, dài 71.105.313 byte và có SHA-256 `AAB87C47250792CC16C3FC23CB3226453D15AE012FA0F0AF003D81F1CBAFE1AF`. Manifest ghi đúng model `6.2.1` và SHA-256 model đã ghim. Gói này thay gói audit `1.2`, đã qua Premiere round-trip A1–A7 và chỉ còn thiếu bằng chứng Windows sạch.

## Các cổng còn thiếu

- Kiểm tra marker `Cần kiểm tra` tại một fragment energy/VAD conflict nếu cần thêm bằng chứng giao diện.
- Chạy ZIP candidate `1.3` đã publish ở chế độ offline trên Windows 10 x64 và Windows 11 x64 sạch, không có .NET/Python cài sẵn.

Nhãn Mục tiêu đã được chủ dự án miễn ngày 2026-08-04. Việc miễn cổng này không được diễn giải thành đã đạt 100% direct speech, 90% clear noise/bleed hoặc 0% ambiguous bị Disable.

Không tạo installer và không merge Phase 06 cho đến khi cổng Windows sạch có đủ bằng chứng.
