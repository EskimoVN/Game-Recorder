# Arma 3 Eskimo: bất tử, vô hạn đạn, không HUD

`OdxArma3Eskimo.exe` là launcher Arma 3 của công ty, thêm mod `@OdxEskimo`.
Mod cho nhân vật bất tử, đạn không bao giờ hết và ẩn HUD khi quay.
Phần quay camera của công ty giữ nguyên.

## Chỉ dùng ở đâu

- Server riêng của Odx hoặc Eskimo, LAN, hoặc máy mình tự host.
- KHÔNG dùng trên server công khai hay server bật BattlEye.
- Mod tự tắt khi tên server không bắt đầu bằng "Odx" hoặc "Eskimo".

## Cách chạy

1. Tải `OdxArma3Eskimo.exe` từ release `eskimo-arma3-v…`. Chép vào đâu cũng được.
2. Mở file. Chọn thư mục game nếu launcher không tự tìm ra.
3. Chọn cách chơi:
   - Vào server Odx: điền `ip:port` và mật khẩu.
   - Tự host hoặc chơi một mình: để trống ô Server.
4. Bấm **Launch**. Game mở với hai mod `@OdxCapture` và `@OdxEskimo`.
5. Khi tự host bằng Multiplayer → Host Server, đặt tên server bắt đầu bằng **Eskimo**. Ví dụ: `Eskimo LAN 1`.
   Đặt sai tên thì máy vào sau không được bất tử, đạn và HUD như cũ.

## Mod làm gì

- Bất tử: bật lại mỗi giây, kể cả sau khi hồi sinh.
- Vô hạn đạn: bắn xong băng đạn được nạp đầy. Lựu đạn, mìn được trả lại.
  Súng trên xe chỉ được nạp lại khi xe do máy mình điều khiển.
- Ẩn HUD: thông tin súng, tâm ngắm, la bàn, radar, chỉ báo nhóm, thông báo chết, khung chat, phụ đề.
- Vẫn giữ menu cuộn chuột, để mở cửa và lên xe.

## Kiểm tra nhanh sau khi vào game

- Không thấy tâm ngắm, la bàn, thông tin súng.
- Bắn hết một băng mà đạn không giảm.
- File `.rpt` mới nhất trong `%LOCALAPPDATA%\Arma 3` có dòng `ESKIMO,on,…`.
  Thấy `ESKIMO,off,…` nghĩa là tên server sai.

## Lưu ý

- `OdxArma3.exe` của công ty vẫn dùng được, nhưng không có mod Eskimo.
- Một số mission tự vẽ HUD riêng, phần đó có thể vẫn hiện. Gặp mission như vậy thì báo lại.
