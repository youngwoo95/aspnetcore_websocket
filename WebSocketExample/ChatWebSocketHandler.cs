using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

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
                    // 메시지 수신 (프레임 처리 및 분할 메시지 조립)
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Text) 
                    {
                        var receivedText = Encoding.UTF8.GetString(buffer, 0, result.Count);

                        // 서브 프로토콜에 따라 분기 : stomp 모드면 STOMP 프레임 처리, 아니면 JSON 처리
                        if (webSocket.SubProtocol?.Equals("stomp", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            await ProcessStompFrameAsync(webSocket, receivedText,user);
                        }
                        else
                        {
                            await ProcessJsonMessageAsync(webSocket, receivedText, user);
                        }
                    }
                    else if(result.MessageType == WebSocketMessageType.Close)
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
        /// 받은 메시지 JSON 프레임 처리
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="receivedText"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        private async Task ProcessJsonMessageAsync(WebSocket webSocket, string receivedText, ClaimsPrincipal user)
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
        /// STOMP 프레임 처리
        /// </summary>
        /// <param name="websocket"></param>
        /// <param name="frame"></param>
        /// <returns></returns>
        private async Task ProcessStompFrameAsync(WebSocket websocket, string frame, ClaimsPrincipal user)
        {
            // STOMP 프레임은 여러 줄로 구성되고 마지막에 null 문자 (\0)이 있음.
            var lines = frame.Split(new[] { "\n" }, StringSplitOptions.None);
            if(lines.Length == 0)
            {
                await SendErrorAsync(websocket, "Empty STOMP frame");
                return;
            }

            // 첫 번째 줄이 명령어
            var command = lines[0].Trim().ToUpper();
            switch(command)
            {
                case "CONNECT":
                    await HandleStompConnectAsync(websocket, lines);
                    break;
                case "SUBSCRIBE":
                    await HandleStompSubscribeAsync(websocket, lines,user);
                    break;
                case "SEND":
                    await HandleStompSendAsync(websocket, lines);
                    break;
                case "DISCONNECT":
                    await HandleStompDisconnectAsync(websocket, lines);
                    break;
                default:
                    await SendErrorAsync(websocket, "Unknown STOMP command:" + command);
                    break;
                    
            }
        }

        private async Task HandleStompConnectAsync(WebSocket webSocket, string[] lines)
        {
            // CONNECT 처리 후 CONNECTED 프레임 전송
            var response = "CONNECTED\nversion:1.2\n\n\0";
            await SendAsync(webSocket, response);
        }

        private async Task HandleStompSubscribeAsync(WebSocket webSocket, string[] lines, ClaimsPrincipal user)
        {
            // STOMP SUBSCRIBE: destination 및 id 헤더 추출
            string destination = string.Empty;
            string subscriptionId = string.Empty;

            foreach(var line in lines)
            {
                if(line.StartsWith("destination:", StringComparison.OrdinalIgnoreCase))
                {
                    destination = line.Substring("destination:".Length).Trim();
                }
                if(line.StartsWith("id:",StringComparison.OrdinalIgnoreCase))
                {
                    subscriptionId = line.Substring("id:".Length).Trim();
                }
            }

            if(String.IsNullOrEmpty(destination) || string.IsNullOrEmpty(subscriptionId))
            {
                await SendErrorAsync(webSocket, "SUBSCRIBE frame missing destination or id");
                return;
            }

            // 구독은 그룹 가입과 유사하게 처리할 수 있음
            // 구독은 그룹 가입과 유사하게 처리할 수 있음 (여기서는 단순 관리)
            var group = _groups.GetOrAdd(destination, _ => new ConcurrentDictionary<string, ClientConnection>());
            var userId = GetUserId(webSocket, user); // 실제로 ClaimsPrincipal을 연결해야 함
            if (!string.IsNullOrEmpty(userId))
            {
                group[userId] = new ClientConnection { Socket = webSocket, UserId = userId };
            }

            // Receipt 프레임 전송 (선택 사항)
            var receiptFrame = $"RECEIPT\nreceipt-id:{subscriptionId}\n\n\0";
            await SendAsync(webSocket, receiptFrame);
        }

        private async Task HandleStompSendAsync(WebSocket webSocket, string[] lines)
        {
            // STOMP SEND : destination 헤더와 본문 추출
            string destination = null;
            int bodyIndex = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    bodyIndex = i + 1;
                    break;
                }
                if (lines[i].StartsWith("destination:", StringComparison.OrdinalIgnoreCase))
                {
                    destination = lines[i].Substring("destination:".Length).Trim();
                }
            }

            if (string.IsNullOrEmpty(destination))
            {
                await SendErrorAsync(webSocket, "SEND frame missing destination");
                return;
            }

            // 본문 (body) 추출 (마지막 null 문자 제거)
            var bodyBuilder = new StringBuilder();
            for (int i = bodyIndex; i < lines.Length; i++)
            {
                bodyBuilder.AppendLine(lines[i].Replace("\0", ""));
            }
            var messageBody = bodyBuilder.ToString().TrimEnd();
            Console.WriteLine(messageBody);
            // 해당 destination(그룹)의 모든 구독자에게 MESSAGE 프레임 전송
            if(_groups.TryGetValue(destination, out var group))
            {
                foreach(var client in group.Values)
                {
                    var messageFrame = $"MESSAGE\ndestination:{destination}\n\n{messageBody}\0";
                    await SendAsync(client.Socket, messageFrame);
                }
            }

            Console.WriteLine("STOMP SEND received. Destination: " + destination + ", Message: " + messageBody);
        }

        private async Task HandleStompDisconnectAsync(WebSocket webSocket, string[] lines)
        {
            // 연결 종료 처리
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
        }

        /// <summary>
        /// 그룹 탈퇴 (JSON)
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

        /// <summary>
        /// 그룹가입 (JSON)
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
        /// 메시지 전송 (JSON)
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

            var userId = GetUserId(webSocket, user);
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
