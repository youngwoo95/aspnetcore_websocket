using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text;

namespace WebSocketExample
{
    public class ChatWebSocketHandler
    {
        // 그룹별 클라이언트 연결 관리 : 그룹명 -> (userId -> ClientConnection)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ClientConnection>> _groups = new();

        // 모든 연결 관리 - userId -> WebSocket
        private readonly ConcurrentDictionary<string, WebSocket> _allConnections = new();

        /// <summary>
        /// 클라이언트 연결을 처리하고 메시지를 수신한다.
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public async Task HandleAsync(WebSocket webSocket, ClaimsPrincipal user)
        {
            var userId = GetUserId(webSocket, user);
            if (string.IsNullOrEmpty(userId))
            {
                // userId가 없으면 연결 종료
                await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "User id not found", CancellationToken.None);
                return;
            }

            // 전체 연결에 추가 (키: userId)
            _allConnections.TryAdd(userId, webSocket);

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
                // 연결 종료 시, 모든 그룹에서 해당 사용자 제거
                RemoveConnectionFromAllGroups(webSocket, user);
                _allConnections.TryRemove(userId, out _); // 전체연결에서 삭제
            }
        }

        /// <summary>
        /// 그룹 탈퇴
        /// </summary>
        /// <param name="socket"></param>
        // 모든 그룹에서 해당 소켓 제거 (소켓에서 userId를 추출)
        private void RemoveConnectionFromAllGroups(WebSocket socket, ClaimsPrincipal user)
        {
            var userId = GetUserId(socket, user);
            foreach (var groupKey in _groups.Keys.ToList())
            {
                if (_groups.TryGetValue(groupKey, out var group))
                {
                    group.TryRemove(userId, out _);
                    if (group.IsEmpty)
                    {
                        _groups.TryRemove(groupKey, out _);
                    }
                }
            }

            Console.WriteLine(GetGroupStatus());
        }

        /// <summary>
        /// 수신한 JSON 메시지를 역직렬화하고 Command에 따라 처리한다.
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="receivedText"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        private async Task ProcessMessageAsync(WebSocket webSocket, string receivedText, ClaimsPrincipal user)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                // 받은 텍스트 디코딩
                var data = JsonSerializer.Deserialize<ChatMessage>(receivedText, options);


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
        /// 그룹에 가입
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="data"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        private async Task HandleJoinAsync(WebSocket webSocket, ChatMessage data, ClaimsPrincipal user)
        {
            if (string.IsNullOrEmpty(data.group))
            {
                await SendErrorAsync(webSocket, "Group name is required for join");
                return;
            }

            var userId = GetUserId(webSocket, user);
            if (string.IsNullOrEmpty(userId))
            {
                await SendErrorAsync(webSocket, "User id not found");
                return;
            }

            var allowedRole = GetRole(webSocket, user);
            if (string.IsNullOrEmpty(allowedRole) || allowedRole != "Manager")
            {
                await SendErrorAsync(webSocket, "You do not have permission to join this group");
                return;
            }

            var group = _groups.GetOrAdd(data.group, _ => new ConcurrentDictionary<string, ClientConnection>());
            // 기존 연결이 있다면 제거하고 닫음
            if (group.TryRemove(userId, out var existing))
            {
                if (existing.Socket.State == WebSocketState.Open)
                {
                    await existing.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Replaced by new connection", CancellationToken.None);
                }
            }
            group[userId] = new ClientConnection { Socket = webSocket, UserId = userId };

            Console.WriteLine(GetGroupStatus());
        }

        /// <summary>
        /// 그룹 나갔을때 처리
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task HandleLeaveAsync(WebSocket webSocket, ChatMessage data, ClaimsPrincipal user)
        {
            if (string.IsNullOrEmpty(data.group))
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
                    // 그룹이 비었으면 삭제
                    if (group.IsEmpty)
                    {
                        _groups.TryRemove(leaveGroupName, out _);
                    }
                    var confirmation = new { info = $"You have left group '{leaveGroupName}'." };
                    await SendAsync(webSocket, JsonSerializer.Serialize(confirmation));
                }
            }
            else
            {
                await SendErrorAsync(webSocket, $"Group '{leaveGroupName}' does not exist.");
            }
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
        /// 클라이언트에서 보낸 메시지 수신
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="data"></param>
        /// <param name="user"></param>
        /// <returns></returns>
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

            // 메시지 내용 출력
            Console.WriteLine(chatMsg);
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
        /// 그룹별 브로드캐스트: 특정 그룹에 가입한 모든 클라이언트에게 메시지 전송
        /// </summary>
        public async Task BroadcastGroupAsync(string groupName, string message)
        {
            if (_groups.TryGetValue(groupName, out var group))
            {
                byte[] outgoing = Encoding.UTF8.GetBytes(message);
                foreach (var kvp in group)
                {
                    var connection = kvp.Value;
                    if (connection.Socket.State == WebSocketState.Open)
                    {
                        await connection.Socket.SendAsync(new ArraySegment<byte>(outgoing),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                    }
                }
            }
            else
            {
                Console.WriteLine($"Group '{groupName}' does not exist.");
            }
        }

        /// <summary>
        /// 모든 연결 중, 그룹에 가입하지 않은 클라이언트에게 브로드캐스트
        /// </summary>
        public async Task BroadcastNonGroupMembersAsync(string message)
        {
            byte[] outgoing = Encoding.UTF8.GetBytes(message);
            foreach (var kvp in _allConnections)
            {
                string userId = kvp.Key;
                WebSocket socket = kvp.Value;

                // 모든 그룹 중에 userId가 포함되어 있는지 검사
                bool isInGroup = _groups.Values.Any(group => group.ContainsKey(userId));
                if (!isInGroup && socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(new ArraySegment<byte>(outgoing),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
        }


        //  JWT 에서 UserId 뽑아낸다.
        private string GetUserId(WebSocket socket, ClaimsPrincipal user)
        {
            var currentUserId = user.FindFirst("userId")?.Value;
            return currentUserId;
        }

        // JWT 에서 ROLE 추출
        private string GetRole(WebSocket socket, ClaimsPrincipal user)
        {
            var allowedGroupsClaim = user.FindFirst("Role")?.Value;
            return allowedGroupsClaim;
        }

    }
}
