# Quick Match: luồng và trạng thái

Tài liệu này mô tả luồng Find Room hiện tại trong `Assets/Script/manager/MainManager.cs`. Quick Match dành cho phòng 2 người và độc lập với luồng tạo phòng thủ công bằng Relay join code.

## Hai luồng riêng biệt

| Thao tác | Nút chính | Khi chờ | Khi có người/phòng | Kết quả |
|---|---|---|---|---|
| Tạo phòng thủ công | Play | `Cancel Room` | `Connecting...`, không cho hủy | Host chờ người vào bằng code; khi đủ người tiếp tục chọn nhân vật/chơi |
| Tìm trận nhanh | Find Room | `Cancel Search` | `Connecting...`, không cho hủy | Vào phòng chọn nhân vật sau khi session và mạng sẵn sàng |
| Join bằng code | Join Room | Join đang xử lý | Không cho đóng/khởi tạo thao tác trùng | Vào luồng chọn nhân vật hiện có |

Không dùng Play để hủy tìm trận. Play chỉ hủy phòng do chính người chơi tạo; Find Room chỉ bắt đầu/hủy tìm Quick Match.

## Các pha Quick Match

1. `Idle`: chưa tìm. Find Room bắt đầu tìm; Play và Join Room không tham gia luồng này.
2. `CheckingOpenRooms`: tìm tối đa khoảng 2 giây trong các Lobby công khai còn chỗ, giới hạn 2 người; mỗi lượt cách nhau 500 ms. Nút là `Cancel Search`.
3. `WaitingForMatchmaker`: không có phòng mở phù hợp, tạo ticket vào queue `QuickMatch-2P` (queue có thể cấu hình trong Inspector). Ticket được poll mỗi giây; nút vẫn là `Cancel Search`.
4. `Connecting`: đã phát hiện phòng mở hoặc Matchmaker trả assignment. UI đổi sang `Connecting...` và khóa hủy trước khi join session/Relay. Không đóng panel hay rời luồng trong lúc khởi tạo kết nối.
5. `InLobby`: session đã kết nối; mở màn hình chọn nhân vật. Find Room không còn là nút hủy; người chơi dùng luồng thoát phòng hiện có.
6. `Canceling`: đang xác minh/hủy ticket. UI khóa thao tác cho đến khi server xác nhận kết quả.

## Quy tắc hủy và các race condition

- Khi đang dò phòng hoặc chờ ticket chưa được ghép, người chơi có thể yêu cầu hủy.
- Trước khi xóa ticket, client đọc trạng thái cuối. Nếu assignment đã xuất hiện, kết quả ghép trận thắng: chuyển ngay sang `Connecting...` và tiếp tục kết nối.
- Nếu ticket chưa được gán, client xóa ticket rồi reset UI về trạng thái ban đầu.
- Nếu ticket biến mất trong lúc xác minh/xóa, thử đọc lại trạng thái. `EntityNotFound`/21404 ở đúng đoạn race hủy được xem là ticket đã được tiêu thụ hoặc đã bị xóa; không tự suy ra là ghép thành công nếu không có assignment.
- Nếu phản hồi đọc/xóa không chắc chắn, giữ thao tác khóa và thử lại trạng thái; không xóa mù ticket vì có thể làm người còn lại bị bỏ lại.
- Khi tìm thấy phòng công khai, UI chuyển sang `Connecting...` trước lời gọi join. Nếu phòng vừa bị đóng/đầy (`SessionNotFound`, `SessionDeleted`, `SessionConflict`), quay lại dò phòng rồi mới fallback Matchmaker.
- Yêu cầu đóng Multiplayer panel trong lúc tìm cũng dùng quy trình hủy an toàn. Nếu ghép trận thắng race, giữ màn hình kết nối/chọn nhân vật để không bỏ rơi người còn lại.
- Khi đã vào `Connecting`, nút hủy và đóng panel bị bỏ qua. Nếu kết nối thất bại/timeout, cleanup session rồi đưa về menu với `Try Again`.

## Cách tạo phòng thủ công

- Người tạo đợi ở màn chọn nhân vật; Play hiển thị `Cancel Room`.
- Nếu người thứ hai vào, Play chuyển `Connecting...` và bị khóa.
- Nếu người tạo bấm hủy đúng lúc người khác vừa vào, client refresh session và kiểm tra số người trước khi rời. Nếu đã có người, giữ phòng và tiếp tục kết nối; nếu chưa, rời session và reset trạng thái.
- Người tìm Quick Match có thể join phòng công khai còn chỗ trước khi tạo Matchmaker ticket. Vì vậy host tạo phòng công khai có thể được Quick Match tìm thấy, không cần đợi queue tạo cặp.

## Điều kiện cấu hình

- Hai client phải dùng cùng Unity Cloud Project ID/environment, cùng queue `QuickMatch-2P`, và Player ID khác nhau.
- Matchmaker queue/configuration phải bật và có thể tạo assignment 2 người. QoS server cần có annotation phù hợp queue; nếu thiếu, bước lấy QoS có thể thất bại trước khi ticket được tạo.
- Phòng để Quick Match tìm thấy phải public, chưa khóa, còn chỗ và có `MaxPlayers == 2`.
- Chọn nhân vật chỉ mở sau khi session/lobby và network đã sẵn sàng; assignment chưa đồng nghĩa Relay/NGO đã kết nối xong.

## Checklist kiểm thử hai client

1. Hai Player ID khác nhau, cùng project/environment và cùng queue.
2. Cả hai bấm Find Room gần nhau: cả hai chuyển sang Connecting rồi vào chọn nhân vật.
3. Một bên bấm Cancel Search trước khi có assignment: bên đó trở về menu; phía còn lại tiếp tục tìm/được ghép bình thường.
4. Bấm Cancel Search ngay lúc gần ghép: nếu assignment đã phát hành thì nút phải khóa và cả hai tiếp tục kết nối; nếu chưa thì ticket bị hủy và UI reset.
5. Một host tạo phòng công khai, client bấm Find Room: client join phòng đó và hiện chọn nhân vật.
6. Host bấm Cancel Room trước khi có người: phòng đóng và UI reset; nếu người join vừa tới thì hủy bị từ chối và host tiếp tục.
7. Đóng panel khi đang tìm, đang Connecting, hoặc đã ở lobby: hủy an toàn ở pha tìm; không cắt ngang kết nối/ghép đã được xác nhận.
8. Làm phòng candidate biến mất/đầy trong lúc join: tìm tiếp hoặc chuyển sang Matchmaker, không treo UI.
9. Test lỗi mạng/Relay và thử lại sau khi cleanup; kiểm tra Console để không còn ticket/session cũ.

Các bước trên cần xác nhận runtime trên hai client thật hoặc hai Editor với hai Unity Authentication Player ID riêng. Build thành công không thay thế kiểm thử dịch vụ online.
