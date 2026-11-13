# Arma 3 Eskimo: bất tử, vô hạn băng đạn, không HUD

`OdxArma3Eskimo.exe` là launcher Arma 3 0.2.3 của công ty, thêm mod `@OdxEskimo`.
Phần của công ty giữ nguyên. Mod Eskimo chỉ thêm bất tử, vô hạn băng đạn và xoá nốt phần HUD công ty chưa xoá.

## Chỉ dùng ở đâu

- Chơi một mình, máy mình tự host, hoặc server Eskimo trong LAN.
- KHÔNG dùng trên server công khai hay server bật BattlEye.
- Mod Eskimo tự tắt khi tên server không bắt đầu bằng "Eskimo".
- Trên server Odx của công ty, mod Eskimo cũng tắt. Luật server công ty giữ nguyên.

## Cách chạy

1. Tải `OdxArma3Eskimo.exe` từ release `eskimo-arma3-v…`. Chép vào đâu cũng được.
2. Tắt Arma 3 nếu đang mở. Launcher không chạy khi game đang mở.
3. Mở file. Chọn thư mục game nếu launcher không tự tìm ra.
4. Chọn cách chơi:
   - Vào máy người khác host: điền `ip:2302` và mật khẩu.
     Dòng chữ dưới cửa sổ của máy host cho biết IP Radmin VPN hoặc ZeroTier cần điền.
   - Tự host hoặc chơi một mình: để trống ô Server.
5. Bấm **Launch**. Lần đầu Windows hỏi quyền admin để mở firewall cho game. Bấm Yes.
6. Game mở với hai mod `@OdxCapture` và `@OdxEskimo`.
7. Khi tự host bằng Multiplayer → Host Server, đặt tên server bắt đầu bằng **Eskimo**. Ví dụ: `Eskimo LAN 1`.
   Đặt sai tên thì máy vào sau không được bất tử, không có vô hạn băng đạn.

## Phần nào của ai

Của công ty, có sẵn trong `@OdxCapture`:
- Ghi camera.
- Ẩn HUD của game lúc vào trận và sau mỗi lần hồi sinh.
- 5 mission capture cài vào thư mục `MPMissions` của game.
- Mở firewall, hiện IP VPN.

Của Eskimo, trong `@OdxEskimo`:
- Bất tử với mọi mission, bật lại mỗi giây.
- Vô hạn băng đạn: bắn hết thì thay đạn bình thường. Mỗi lần thay, kho đồ được thêm lại một băng cùng loại, nên không bao giờ hết băng. Lựu đạn, mìn được trả lại.
  Súng trên xe chỉ được nạp lại khi xe do máy mình điều khiển.
- Ẩn lại HUD ở mọi khung hình, phòng khi mission bật HUD lên giữa trận.
- Xoá thứ công ty chưa xoá: hint, tip, thông báo, khung chat, phụ đề.
- Xoá HUD do mission tự vẽ, ví dụ tiền CP và thu nhập mỗi phút của Warlords.

## Kiểm tra nhanh sau khi vào game

- Màn hình không còn chữ hay biểu tượng nào. Không thấy tiền CP, tip, tâm ngắm.
- Bắn hết một băng mà đạn không giảm.
- File `.rpt` mới nhất trong `%LOCALAPPDATA%\Arma 3` có dòng `ESKIMO,on,…`.
  Thấy `ESKIMO,off,…` nghĩa là tên server sai.

## Lưu ý

- Công ty ẩn cả menu cuộn chuột. Có thể không mở cửa, không lên xe bằng menu được.
- Màn hình chờ hồi sinh cũng bị xoá, nên lúc hồi sinh có thể thấy cảnh game thay vì màn đen.
- `OdxArma3.exe` của công ty vẫn dùng được, nhưng không có bất tử và vô hạn băng đạn ở mission khác.
