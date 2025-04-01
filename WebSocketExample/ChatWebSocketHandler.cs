using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text;

namespace WebSocketExample
{
    public class ChatWebSocketHandler
    {
        // 그룹별 클라이언트 연결 관리
        private readonly ConcurrentDictionary<string, List<ClientConnection>> _groups = new();
        // 메시지 큐
        private readonly ConcurrentQueue<ChatMessage> _messageQueue = new();

        public ChatWebSocketHandler()
        {
            // 메시지 큐 처리 백그라운드 작업 시작
            _ = ProcessMessageQueueAsync();
        }

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
                RemoveConnectionFromAllGroups(webSocket);
            }
        }

        private async Task ProcessMessageAsync(WebSocket webSocket, string receivedText, ClaimsPrincipal user)
        {
            try
            {
                // 받은 텍스트 디코딩
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(receivedText);

                // 디코딩에 실패하지 않았고, command라는 key가 존재하는지 검사
                if (data == null || !data.TryGetValue("command", out var command))
                {
                    await SendErrorAsync(webSocket, "Invalid message format");
                    return;
                }

                if (data.TryGetValue("message", out var textMessage) && textMessage.Length > 500)
                {
                    await SendErrorAsync(webSocket, "메시지 전송 용량이 초과되었습니다.");
                    return;
                }

                switch (command)
                {
                    case "join":
                        // 그룹 가입
                        await HandleJoinAsync(webSocket, data, user);
                        break;
                    case "leave":
                        // 그룹 탈퇴
                        await HandleLeaveAsync(webSocket, data);
                        break;
                    case "send":
                        // 메시지 전송
                        await HandleSendAsync(webSocket, data, user);
                        break;
                    default:
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

        private async Task HandleJoinAsync(WebSocket webSocket, Dictionary<string, string> data, ClaimsPrincipal user)
        {
            // group 파라미터를 찾아서 - 그룹으로 가입시킨다.
            if (!data.TryGetValue("group", out var joinGroupName))
            {
                await SendErrorAsync(webSocket, "Group name is required for join");
                return;
            }
            // group 가입시킬때 토큰에 UserId를 뽑아낸다.
            var userId = user.FindFirst("userId")?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                await SendErrorAsync(webSocket, "User id not found");
                return;
            }

            // 그룹 참여 권한 체크(필요시 구현)
            // Role이 Manager인지 검사
            var allowedGroupsClaim = user.FindFirst("Role")?.Value;
            if (string.IsNullOrEmpty(allowedGroupsClaim) || allowedGroupsClaim != "Manager")
            {
                await SendErrorAsync(webSocket, "You do not have permission to join this group");
                return;
            }

            // 그룹관리
            var groupList = _groups.GetOrAdd(joinGroupName, _ => new List<ClientConnection>());
            lock (groupList)
            {
                if (!groupList.Any(c => c.Socket == webSocket))
                {
                    // 그룹에 가입시킨다.
                    groupList.Add(new ClientConnection { Socket = webSocket, UserId = userId });
                }
            }
        }

        /// <summary>
        /// 그룹 나갔을때 처리
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        private async Task HandleLeaveAsync(WebSocket webSocket, Dictionary<string, string> data)
        {
            if (!data.TryGetValue("group", out var leaveGroupName)) 
            {
                await SendErrorAsync(webSocket, "Group name is required for leave");
                return;
            }

            if (_groups.TryGetValue(leaveGroupName, out var groupList))
            {
                bool removed = false;
                lock (groupList)
                {
                    var connection = groupList.FirstOrDefault(c => c.Socket == webSocket);
                    if (connection != null)
                    {
                        groupList.Remove(connection);
                        removed = true;
                    }
                }
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

        private async Task HandleSendAsync(WebSocket webSocket, Dictionary<string, string> data, ClaimsPrincipal user)
        {
            if (!data.TryGetValue("group", out var sendGroupName) || !data.TryGetValue("message", out var messageText))
            {
                await SendErrorAsync(webSocket, "Group and message are required for send");
                return;
            }

            if (string.IsNullOrWhiteSpace(messageText))
            {
                await SendErrorAsync(webSocket, "Empty message");
                return;
            }

            // 그룹 존재 여부 확인
            // 그룹이 존재하는지 검사
            if (!_groups.TryGetValue(sendGroupName, out var groupList))
            {
                await SendErrorAsync(webSocket, $"Group '{sendGroupName}' does not exist.");
                return;
            }

            // 전송 권한 검사
            // Role이 Manager인지 검사
            var allowedGroupsClaim = user.FindFirst("Role")?.Value;
            if (string.IsNullOrEmpty(allowedGroupsClaim) || allowedGroupsClaim != "Manager")
            {
                await SendErrorAsync(webSocket, "You do not have permission to join this group");
                return;
            }

            bool isMember;
            lock (groupList)
            {
                // 그룹 내 회원 여부 확인
                // 소켓의 그룹 멤버 여부 검사
                isMember = groupList.Any(c => c.Socket == webSocket);
            }
            if (!isMember)
            {
                await SendErrorAsync(webSocket, $"You are not a member of group '{sendGroupName}'.");
                return;
            }
            var UserId = user.FindFirst("UserId")?.Value;
            // 메시지 큐에 담기
            var chatMsg = new ChatMessage
            {
                Group = sendGroupName,
                Message = $"보내는 사람 ID: {UserId} / 메시지 내용 : {messageText}"
            };
            _messageQueue.Enqueue(chatMsg);
        }

        // 메시지 전송
        private async Task ProcessMessageQueueAsync()
        {
            while (true)
            {
                if (_messageQueue.TryDequeue(out ChatMessage chatMsg))
                {
                 

                    if (_groups.TryGetValue(chatMsg.Group, out var connections))
                    {
                        List<ClientConnection> closedConnections = new();
                        lock (connections)
                        {
                            foreach (var connection in connections)
                            {
                                if (connection.Socket.State == WebSocketState.Open)
                                {
                                    Console.WriteLine(connection.UserId + " / " + chatMsg);

                                    var outgoing = Encoding.UTF8.GetBytes(chatMsg.Message);
                                    _ = connection.Socket.SendAsync(new ArraySegment<byte>(outgoing),
                                        WebSocketMessageType.Text,
                                        true,
                                        CancellationToken.None);
                                }
                                else
                                {
                                    closedConnections.Add(connection);
                                }
                            }
                            foreach (var closed in closedConnections)
                            {
                                connections.Remove(closed);
                            }
                        }
                    }
                }
                else
                {
                    await Task.Delay(100);
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
        private void RemoveConnectionFromAllGroups(WebSocket socket)
        {
            foreach (var group in _groups)
            {
                lock (group.Value)
                {
                    var connection = group.Value.FirstOrDefault(c => c.Socket == socket);

                    if (connection != null)
                    {
                        Console.WriteLine($"그룹명 : {group.Key} / 유저ID {connection.UserId} 그룹탈퇴");
                        group.Value.Remove(connection);
                    }
                }
            }
        }

        /// <summary>
        /// 서버는 여기 Queue에 담아 보냄
        /// </summary>
        /// <param name="chatMsg"></param>
        public void EnqueueMessage(ChatMessage chatMsg)
        {
            _messageQueue.Enqueue(chatMsg);
        }

    }
}
