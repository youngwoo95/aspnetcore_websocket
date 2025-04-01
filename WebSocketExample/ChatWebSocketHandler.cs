using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using System.Net.Sockets;
using Microsoft.IdentityModel.Tokens;

namespace WebSocketExample
{
    public class ChatWebSocketHandler
    {
        // 그룹별 클라이언트 연결 관리 : 그룹명 -> (userId -> ClientConnection)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string,ClientConnection>> _groups = new();

        // 메시지 큐를 ConcurrentQueue 대신 Channel 을 사용
        private readonly Channel<ChatMessage> _channel = Channel.CreateUnbounded<ChatMessage>();

        // 연결&가입
        public async Task HandleAsync(WebSocket webSocket, ClaimsPrincipal user)
        {
            var buffer = new byte[1024 * 4];
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var receivedText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await ProcessMessageAsync(webSocket, receivedText, user);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (WebSocketException ex)
            {
                // 예외 발생 시 로그 기록 후 종료 (예: 클라이언트가 갑자기 끊어진 경우)
                Console.WriteLine("WebSocket exception: " + ex.Message);
            }
            finally
            {
                // 연결 종료 또는 예외 발생 시 모든 그룹에서 해당 소켓 제거
                RemoveConnectionFromAllGroups(webSocket, user);
            }
        }

        private async Task ProcessMessageAsync(WebSocket webSocket, string receivedText, ClaimsPrincipal user)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                // 받은 텍스트 디코딩
                //var data = JsonSerializer.Deserialize<Dictionary<string, string>>(receivedText);
                var data = JsonSerializer.Deserialize<ChatMessage>(receivedText);



                // 디코딩에 실패하지 않았고, command라는 key가 존재하는지 검사
                if (data == null || String.IsNullOrEmpty(data.command))
                {
                    await SendErrorAsync(webSocket, "Invalid message format");
                    return;
                }

                if(!String.IsNullOrEmpty(data.message) && data.message.Length > 500)
                {
                    await SendErrorAsync(webSocket, "메시지 전송 용량이 초과되었습니다.");
                    return;
                }
             
                // command에 따라 처리
                switch (data.command.ToLower())
                {
                    case "join":
                        // 그룹 가입
                        await HandleJoinAsync(webSocket, data, user);
                        break;
                    case "leave":
                        // 그룹 탈퇴
                        await HandleLeaveAsync(webSocket, data, user);
                        break;
                    case "send":
                        // 메시지 전송
                        await HandleSendAsync(webSocket, data, user);
                        break;
                    default:
                        // 에러 메시지
                        await SendErrorAsync(webSocket, "Unknown command");
                        break;
                }
            }
            catch (JsonException ex)
            {
                await SendErrorAsync(webSocket, "JSON parsing error: " + ex.Message);
            }
            catch (Exception ex)
            {
                await SendErrorAsync(webSocket, "Error: " + ex.Message);
            }
        }

        /// <summary>
        /// 연결
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="data"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        private async Task HandleJoinAsync(WebSocket webSocket, ChatMessage data, ClaimsPrincipal user)
        {
            // data.Group에 가입할 그룹명, data.command는 "join"
            if(string.IsNullOrEmpty(data.group))
            {
                await SendErrorAsync(webSocket, "Group name is required for join");
                return;
            }

            // group 가입시킬때 토큰에 UserId를 뽑아낸다.
            var userId = GetUserId(webSocket, user);
            if (string.IsNullOrEmpty(userId))
            {
                await SendErrorAsync(webSocket, "User id not found");
                return;
            }

            // 그룹 참여 권한 체크(필요시 구현)
            // Role이 Manager인지 검사
            var allowedGroupsClaim = GetRole(webSocket, user);
            if (string.IsNullOrEmpty(allowedGroupsClaim) || allowedGroupsClaim != "Manager")
            {
                await SendErrorAsync(webSocket, "You do not have permission to join this group");
                return;
            }

            // 그룹관리
            // 그룹 관리: 그룹별로 ConcurrentDictionary를 사용하여 userId를 키로 저장
            var group = _groups.GetOrAdd(data.group, _ => new ConcurrentDictionary<string, ClientConnection>());
            // 기존 연결이 있다면 제거하고 닫음
            if (group.TryRemove(userId, out var existing))
            {
                if (existing.Socket.State == WebSocketState.Open)
                {
                    await existing.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Replaced by new connection", CancellationToken.None);
                }
            }

            // 새 연결 추가
            group[userId] = new ClientConnection { Socket = webSocket, UserId = userId };

            // 예: 그룹 가입 후 전체 그룹 현황 출력
            Console.WriteLine(GetGroupStatus());
        }

        public string GetGroupStatus()
        {
            var sb = new StringBuilder();
            foreach (var group in _groups)
            {
                sb.AppendLine($"Group: {group.Key}");
                foreach (var userConnection in group.Value)
                {
                    sb.AppendLine($"  UserId: {userConnection.Key}");
                }
            }
            return sb.ToString();
        }
        /// <summary>
        /// 그룹 나갔을때 처리
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task HandleLeaveAsync(WebSocket webSocket, ChatMessage data, ClaimsPrincipal user)
        {

            if(string.IsNullOrEmpty(data.group))
            {
                await SendErrorAsync(webSocket, "Group name is required for leave");
                return;
            }

            var leaveGroupName = data.group;

            if (_groups.TryGetValue(leaveGroupName, out var group))
            {
                var removed = group.TryRemove(GetUserId(webSocket, user), out var connection);
                if (!removed)
                {
                    await SendErrorAsync(webSocket, $"You are not a member of group '{leaveGroupName}'.");
                }
                else
                {
                    var confirmation = new { info = $"You have left group '{leaveGroupName}'." };
                    await SendAsync(webSocket, JsonSerializer.Serialize(confirmation));
                }
            }
            else
            {
                await SendErrorAsync(webSocket, $"Group '{leaveGroupName}' does not exist.");
            }
        }

        // 클라이언트 --> 서버 수신
        private async Task HandleSendAsync(WebSocket webSocket, ChatMessage data, ClaimsPrincipal user)
        {
            // Group과 Message 프로퍼티가 null 또는 빈 문자열인지 검사합니다.
            if (string.IsNullOrEmpty(data.group) || string.IsNullOrEmpty(data.message))
            {
                await SendErrorAsync(webSocket, "Group and message are required for send");
                return;
            }

            if (string.IsNullOrWhiteSpace(data.message))
            {
                await SendErrorAsync(webSocket, "Empty message");
                return;
            }

            if (!_groups.TryGetValue(data.group, out var group))
            {
                await SendErrorAsync(webSocket, $"Group '{data.group}' does not exist.");
                return;
            }

            // 권한 체크
            var allowedGroupsClaim = user.FindFirst("Role")?.Value;
            if (string.IsNullOrEmpty(allowedGroupsClaim) || allowedGroupsClaim != "Manager")
            {
                await SendErrorAsync(webSocket, "You do not have permission to join this group");
                return;
            }

            var userId  = GetUserId(webSocket, user);
            if (!group.ContainsKey(userId))
            {
                await SendErrorAsync(webSocket, $"You are not a member of group '{data.group}'.");
                return;
            }

            var currentUserId = GetUserId(webSocket, user);
            var chatMsg = new ChatMessage
            {
                command = data.command,
                group = data.group,
                message = $"보내는 사람 ID: {currentUserId} / 메시지 내용 : {data.message}"
            };
            Console.WriteLine(chatMsg);

            // 받은 메시지를 그대로 돌려주는 예시
            //EnqueueMessage(chatMsg);
        }

        // 서버 -> 클라이언트 메시지 전송
        // 메시지 큐 처리: Channel을 사용한 비동기 소비자 
        public async Task ProcessMessageQueueAsync(CancellationToken cancellationToken)
        {
            await foreach (var chatMsg in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (_groups.TryGetValue(chatMsg.group, out var group))
                {
                    foreach (var kvp in group)
                    {
                        var connection = kvp.Value;
                        if (connection.Socket.State == WebSocketState.Open)
                        {
                            //Console.WriteLine($"{connection.UserId} / {chatMsg}");
                            var outgoing = Encoding.UTF8.GetBytes(chatMsg.message);
                            // 전송 결과는 fire-and-forget으로 처리
                            _ = connection.Socket.SendAsync(new ArraySegment<byte>(outgoing),
                                    WebSocketMessageType.Text,
                                    true,
                                    CancellationToken.None);
                        }
                        else
                        {
                            // 만약 소켓이 닫혀 있다면 해당 사용자를 제거합니다.
                            group.TryRemove(kvp.Key, out _);
                        }
                    }
                }
            }
        }

        // 에러 메시지 전송
        private async Task SendErrorAsync(WebSocket socket, string errorMessage)
        {
            var errorData = new { error = errorMessage };
            await SendAsync(socket, JsonSerializer.Serialize(errorData));
        }

        private async Task SendAsync(WebSocket socket, string message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        /// <summary>
        /// 그룹 탈퇴
        /// </summary>
        /// <param name="socket"></param>
        // 모든 그룹에서 해당 소켓 제거 (소켓에서 userId를 추출)
        private void RemoveConnectionFromAllGroups(WebSocket socket, ClaimsPrincipal user)
        {
            foreach (var group in _groups)
            {
                group.Value.TryRemove(GetUserId(socket, user), out _);
            }

            // 예: 그룹 가입 후 전체 그룹 현황 출력
            Console.WriteLine(GetGroupStatus());
        }

        /// <summary>
        /// 서버는 여기 Queue에 담아 보냄
        /// </summary>
        /// <param name="chatMsg"></param>
        // EnqueueMessage: Channel Writer를 사용
        public void EnqueueMessage(ChatMessage chatMsg)
        {
            _channel.Writer.TryWrite(chatMsg);
        }


        //  클라이언트의 인증 정보를 기반으로 UserId 뽑아낸다.
        private string GetUserId(WebSocket socket, ClaimsPrincipal user)
        {
            var currentUserId = user.FindFirst("UserId")?.Value;
            return currentUserId;
        }

        private string GetRole(WebSocket socket, ClaimsPrincipal user)
        {
            var allowedGroupsClaim = user.FindFirst("Role")?.Value;
            return allowedGroupsClaim;
        }

    }
}
