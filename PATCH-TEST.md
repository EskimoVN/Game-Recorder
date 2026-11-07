# Bản vá test: capture v0.2.8

Bản này dựa trên `capture-v0.2.8`, sửa 6 file trong `capture/dotnet/src/`.

## Thay đổi

1. **Đồng hồ đếm giờ** trên cửa sổ tool: đỏ khi đang quay, xám và giữ số khi dừng.
2. **Task ID** dạng `<mã lô>-Sxx`: host gõ mã lô (ví dụ `AMRF-001`) một lần. Số video tự tăng S01 → S15 sau mỗi round. Đủ 15 thì phải đổi mã lô.
3. **Toast khi tên sai**: hiện góc phải màn hình, không có tiếng, không lọt vào video.
4. **Cửa sổ tool không lọt vào video** khi quay cả màn hình.
5. **Sửa lỗi dừng quay bị kẹt** (`stop stuck in 'last frames' after 40 s`).

## Build gói 0.2.9 (máy Windows)

Cần:
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Không cần MSYS2 hay Visual Studio.
- Gói chính thức `odx-capture-0.2.8-win-x64.zip` (GitHub release `capture-v0.2.8`).

Các bước:
1. Giải nén zip source này ra máy Windows.
2. Mở PowerShell trong `capture/dotnet/`, chạy (đổi đường dẫn tới file zip 0.2.8):

```
powershell -ExecutionPolicy Bypass -File build-package.ps1 -Base "C:\Users\<ten>\Downloads\odx-capture-0.2.8-win-x64.zip"
```

3. Lần đầu mất khoảng 5–10 phút. Xong, trong `capture/dotnet/dist/` có:

| Kết quả | Dùng khi |
|---|---|
| `odx-capture-0.2.9-win-x64.zip` | Cài mới. Cấu trúc y hệt bản 0.2.8 |
| `odx-capture-0.2.9-update.zip` | Máy đã cài 0.2.8, chỉ thay 2 file exe |

Gói 0.2.9 lấy nguyên từ gói 0.2.8: `Odx Capture.exe`, `bin/odx-capture.json` (server, access key), `bin/ffmpeg/`, `bin/games/`, `data/`. Chỉ `bin/odx-app.exe` và `bin/odx.exe` được build mới. Cuối lần chạy, script in danh sách file kèm nguồn của từng file.

## Cài cho người dùng

**Cài mới:** làm như HDSD, chỉ thay file `odx-capture-0.2.8-win-x64.zip` bằng `odx-capture-0.2.9-win-x64.zip`. Mở `Odx Capture.exe` trong thư mục vừa giải nén. Tool chạy game vẫn dùng `OdxReforger.exe` như cũ.

**Đã cài 0.2.8:** tắt Odx Capture, giải nén `odx-capture-0.2.9-update.zip` vào thư mục `odx-capture-0.2.8-win-x64`, chọn "Replace", mở lại bằng quyền admin.

## Cần test

| # | Làm gì | Kết quả đúng |
|---|---|---|
| 1 | Quay một mình, F10 bắt đầu, F10 dừng | Đồng hồ chạy sau 3-2-1, dừng khi ấn F10 |
| 2 | Host gõ `AMRF-001`, chọn `01`, quay 1 round | Xong round, ô số tự nhảy sang `02` |
| 3 | Để trống mã lô, ấn F10 | Toast báo lỗi, không có tiếng, round không chạy |
| 4 | Quay 15 video cùng một lô | Sau S15 báo lô đã đủ, không cho quay tiếp |
| 5 | Quay nhiều người, ấn F10 dừng | Dừng trong vài giây, không báo `stop stuck` |
| 6 | Mở `video.mp4` của một người | Không thấy đồng hồ, cửa sổ tool hay toast |

## Khi lỗi, gửi lại

- Ảnh thông báo lỗi.
- File `recording.json` của máy lỗi, trong `data/captures/<task-id>/` hoặc `_work/<task-id>/players/<pid>/`.
