# Quick Match: lỗi thường gặp và cách xử lý

Đọc cùng [Quick-Match-Workflow.md](Quick-Match-Workflow.md). Log/stack trace của Unity Services thường dài; ưu tiên dòng đầu tiên có tên lỗi, operation, ticket/session ID và pha matchmaking hiện tại.

## Checklist đầu tiên

1. Xác nhận hai client cùng Unity Cloud Project ID/environment, cùng queue, nhưng Player ID khác nhau.
2. Xác nhận queue `QuickMatch-2P` tồn tại, được cấu hình cho 2 người, và service/environment đang bật.
3. Xem log `[Matchmaking]`: `Checking for an existing open room` → `No joinable open room...` → `Created ticket...` → `assigned match...` → `connected`.
4. Nếu không có log `Created ticket`, lỗi xảy ra trước ticket (Auth, Lobby query, QoS hoặc config). Nếu có assignment nhưng không tới chọn nhân vật, tập trung kiểm tra join session, Relay và NGO.
5. Sau lỗi, kiểm tra UI đã reset/hiện `Try Again` hoặc cooldown chưa; không spam Find Room vì có thể gặp rate limit.

## Các lỗi đã gặp

| Thông báo | Ý nghĩa thường gặp trong luồng này | Xử lý / hành vi mong đợi |
|---|---|---|
| `SessionNotFound` khi join Matchmaker assignment | Cách join cũ coi MatchId như session đã tồn tại, trong khi assignment có thể là rendezvous ID và client đầu tiên phải tạo session | Dùng `CreateOrJoinSessionAsync(matchId, options)` để người đầu tạo Lobby và người kia join. Nếu lỗi xảy ra khi dò phòng công khai, candidate đã biến mất; bỏ qua candidate, tiếp tục dò/fallback queue |
| `SessionDeleted` / `SessionConflict` | Phòng công khai vừa đóng/đầy hoặc có race giữa query và join | Tìm tiếp; không để UI kẹt Connecting. Nếu Matchmaker assignment đã có thì không áp dụng retry kiểu open-room |
| Relay `Request timeout` / `Failed to join allocation` | Session đã được xử lý nhưng Relay allocation không hoàn tất kịp, mạng/service lỗi hoặc session/host không còn hợp lệ | Đợi network startup trong giới hạn timeout; nếu lỗi thì leave/cleanup session, reset UI và cho `Try Again`. Kiểm tra host còn hoạt động, mạng, Unity Services và Project ID |
| `MatchmakerCancelled` / `A task was canceled` | Ticket bị hủy hoặc Matchmaker kết thúc ticket; có thể là hủy hợp lệ, nhưng nếu không có thao tác hủy thì là queue/service đã dừng ticket | Log phân biệt cancellation do người chơi với cancellation từ service. Không mở chọn nhân vật nếu chưa có assignment/session; cleanup ticket/session và thử lại sau khi kiểm tra queue |
| `EntityNotFound (21404)`, `Ticket ... not found` | Ticket đã bị xóa, Matchmaker đã tiêu thụ ticket, hoặc status request đua với thao tác cancel/delete | Trong cancellation flow, xem là race hợp lệ và xác minh lại trạng thái nếu có thể. Không retry ticket cũ vô hạn; tìm lại bằng ticket mới nếu người chơi chủ động thử lại |
| `429 Too Many Requests` | Rate limit do gọi Matchmaker/Lobby quá thường xuyên hoặc test bấm lặp | Hiện cooldown `Wait Before Retry` (hiện 5 giây), dừng bấm liên tục và để poll/backoff hoạt động. Nếu còn lặp, kiểm tra test có nhiều client/ticket bỏ quên không và Dashboard quota |
| `QosJob: took ... ms` | Thời gian đo QoS; tự thân đây là thông tin hiệu năng, không phải lỗi ghép trận | Chỉ điều tra nếu QoS không trả kết quả, thiếu queue/pool annotation, hoặc request sau đó timeout. Không coi riêng dòng này là nguyên nhân |
| Không thấy `Created ticket` | Luồng chưa đi tới Matchmaker; có thể đang join phòng mở, query Lobby lỗi, QoS/Auth lỗi, queue rỗng hoặc thao tác bị hủy sớm | Đọc log trước đó; kiểm tra Unity Services/Auth, internet, queue annotations, và trạng thái nút |
| Tìm lâu, không match | Một ticket đơn lẻ không đủ tạo cặp; phòng mở không phù hợp hoặc queue không ghép được hai ticket | Chạy hai client đồng thời, Player ID khác nhau; xác minh cùng project/environment/queue và queue rule. Một người chơi duy nhất có thể chờ vô hạn theo queue hiện tại |
| Match tìm thấy nhưng không hiện chọn nhân vật | Assignment nhận được chưa đủ; join session/Lobby/Relay/NGO có thể đang chạy hoặc lỗi | UI phải hiện Connecting và disable cancel trước join. Chờ giới hạn network startup; sau lỗi xem `SessionNetworkStartException`, Relay/NGO log và cleanup |

## Cảnh báo khi debug cancel

- Không xóa ticket chỉ vì status poll tạm lỗi hoặc phản hồi DELETE không chắc chắn. Đọc lại ticket trước; nếu assignment xuất hiện thì phải tiếp tục kết nối.
- Không hủy token của một Relay join đã bắt đầu do assignment được xác nhận; điều này từng gây lỗi Relay allocation và có thể bỏ lại peer. Sau assignment, để kết nối hoàn tất hoặc fail theo timeout rồi cleanup.
- `EntityNotFound` không tự nó chứng minh match thành công. Chỉ tiếp tục kết nối khi có `MatchIdAssignment` với trạng thái Found/MatchId hợp lệ.
- Không dùng chung Player ID cho hai client. Hai Editor trên cùng máy chỉ là test hợp lệ nếu mỗi instance có Authentication profile/Player ID riêng và cùng environment.
- Khi test hủy sát thời điểm match, nút có thể tạm hiện `Checking Match...`; sau khi server trả kết quả, nó phải hoặc trở về trạng thái tìm/hủy, hoặc chuyển `Connecting...` không thể hủy.

## Log nên thu thập khi tái hiện

- Toàn bộ đoạn từ `[Matchmaking] Requesting queue...` đến kết quả cuối, trên cả hai client.
- Player ID (không cần chia sẻ token/credential), queue name, Project ID/environment, thời gian và thao tác bấm.
- Ticket ID/session ID nếu có, cùng lỗi gốc đầu tiên (inner exception), không chỉ dòng stack trace cuối.
- Trạng thái nút (`Cancel Search`, `Checking Match...`, `Connecting...`) và màn hình hiện tại của mỗi client.

Không gửi access token, session token, credential hoặc thông tin bí mật trong log chia sẻ.
