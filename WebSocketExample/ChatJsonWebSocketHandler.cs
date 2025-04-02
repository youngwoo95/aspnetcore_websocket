using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebSocketExample
{
    public class ChatJsonWebSocketHandler : IChatWebSocketHandler
    {
        // 그룹별 클라이언트 연결 관리 : 그룹명 -> (userId -> ClientConnection)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ClientConnection>> _groups = new();

        /// <summary>
        /// 클라이언트 연결을 처리하고 메시지를 수신
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public async Task HandleAsync(WebSocket webSocket, ClaimsPrincipal user)
        {
            string userId = GetUserId(user);
            if(string.IsNullOrEmpty(userId))
            {
                // userId가 없으면 연결 종료
                await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "User id not found", CancellationToken.None);
                return;
            }

            var buffer = new byte[1024 * 4];
            try
            {
                while(webSocket.State == WebSocketState.Open)
                {
                    // 메시지 수신 (프레임 처리 및 분할 메시지 조립)
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var receivedText = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await ProcessJsonMessageAsync(webSocket, receivedText, user);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch(WebSocketException ex)
            {
                Console.WriteLine("WebSocket exception: " + ex.Message);
            }
            finally
            {
                RemoveConnectionFromAllGroups(webSocket, user);
            }
        }

        /// <summary>
        /// 그룹 탈퇴
        /// </summary>
        /// <param name="socket"></param>
        // 모든 그룹에서 해당 소켓 제거 (소켓에서 userId를 추출)
        private void RemoveConnectionFromAllGroups(WebSocket socket, ClaimsPrincipal user)
        {
            var userId = GetUserId(user);
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

            var Result = GetJsonGroupStatus();
            if(String.IsNullOrWhiteSpace(Result))
            {
                Console.WriteLine("연결중인 User가 없습니다.");
            }
        }

        /// <summary>
        /// 받은 메시지 처리
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
                var data = JsonSerializer.Deserialize<MessageTemplate>(receivedText, options);

                // 디코딩에 실패하지 않았고, command 라는 key사 존재하는지 검사
                if(data == null || String.IsNullOrEmpty(data.command))
                {
                    await SendJsonErrorAsync(webSocket, "Invalid message format");
                    return;
                }

                if(!String.IsNullOrEmpty(data.message) && data.message.Length > 500)
                {
                    await SendJsonErrorAsync(webSocket, "메시지 전송 용량이 초과되었습니다.");
                    return;
                }

                switch(data.command.ToLower())
                {
                    case "join":
                        await HandleJoinAsync(webSocket, data, user);
                        break;
                    case "leave":
                        await HandleLeaveAsync(webSocket, data, user);
                        break;
                    case "send":
                        await HandleJsonReceiveAsync(webSocket, data, user);
                        break;
                    default:
                        await SendJsonErrorAsync(webSocket, "Unknown command");
                        break;
                }
            }
            catch(JsonException ex)
            {
                await SendJsonErrorAsync(webSocket, "JSON parsing error: " + ex.Message);
            }
        }

        private async Task HandleJoinAsync(WebSocket webSocket, MessageTemplate data, ClaimsPrincipal user)
        {
            if (string.IsNullOrEmpty(data.group))
            {
                await SendJsonErrorAsync(webSocket, "Group name is required for join");
                return;
            }

            var userId = GetUserId(user);
            if (string.IsNullOrEmpty(userId))
            {
                await SendJsonErrorAsync(webSocket, "User id not found");
                return;
            }

            // * 권한관련 처리할때 이렇게하면 됨
            //var allowedRole = GetRole(user);
            //if (string.IsNullOrEmpty(allowedRole) || allowedRole != "Manager")
            //{
            //    await SendJsonErrorAsync(webSocket, "You do not have permission to join this group");
            //    return;
            //}

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

            Console.WriteLine(GetJsonGroupStatus());
        }


        private async Task HandleJsonReceiveAsync(WebSocket webSocket, MessageTemplate data, ClaimsPrincipal user)
        {
            if(string.IsNullOrEmpty(data.group) || string.IsNullOrEmpty(data.message))
            {
                await SendJsonErrorAsync(webSocket, "Group and message are required for send");
                return;
            }

            if(string.IsNullOrWhiteSpace(data.message))
            {
                await SendJsonErrorAsync(webSocket, "Empty message");
                return;
            }

            if(!_groups.TryGetValue(data.group, out var group))
            {
                await SendJsonErrorAsync(webSocket, $"group '{data.group}' does not exist.");
                return;
            }

            // 권한 체크
            /*
            var allowedGroupsClaim = user.FindFirst("Role")?.Value;
            if(string.IsNullOrEmpty(allowedGroupsClaim) || allowedGroupsClaim != "Manager")
            {
                await SendJsonErrorAsync(webSocket, "You do not have permission to join this group");
                return;
            }
            */

            var userId = GetUserId(user);
            if(!group.ContainsKey(userId))
            {
                await SendJsonErrorAsync(webSocket, $"You are not a member of group '{data.group}'.");
                return;
            }

            var currentUserId = GetUserId(user);
            var Message = new MessageTemplate
            {
                command = data.command,
                group = data.group,
                message = $"보내는 사람 ID : {currentUserId} / 메시지 내용 : {data.message}"
            };

            // 메시지 내용 출력
            Console.WriteLine("JSON" + Message.ToString());
        }

        /// <summary>
        /// 그룹 탈퇴
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="data"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        private async Task HandleLeaveAsync(WebSocket webSocket, MessageTemplate data, ClaimsPrincipal user)
        {
            if(string.IsNullOrEmpty(data.group))
            {
                await SendJsonErrorAsync(webSocket, "Group name is required for leave");
                return;
            }

            var leaveGroupName = data.group;
            if(_groups.TryGetValue(leaveGroupName, out var group))
            {
                var removed = group.TryRemove(GetUserId(user), out var connection);
                if(!removed)
                {
                    await SendJsonErrorAsync(webSocket, $"You are not a memeber of group '{leaveGroupName}'.");
                }
                else
                {
                    // 그룹이 비었으면 삭제
                    if(group.IsEmpty)
                    {
                        _groups.TryRemove(leaveGroupName, out _);
                    }
                    var confirmation = new { info = $"You have left group '{leaveGroupName}'." };
                    await SendJsonAsync(webSocket, JsonSerializer.Serialize(confirmation));
                }
            }
            else
            {
                await SendJsonErrorAsync(webSocket, $"Group '{leaveGroupName}' does not exist.");
            }
        }

        public string GetJsonGroupStatus()
        {
            var sb = new StringBuilder();
            foreach(var group in _groups)
            {
                sb.AppendLine($"Group: {group.Value}");
                foreach(var userConnection in group.Value)
                {
                    sb.AppendLine($"   UserId: {userConnection.Key}");
                }
            }
            return sb.ToString();
        }

        private async Task SendJsonErrorAsync(WebSocket socket, string errorMessage)
        {
            var errorData = new { error = errorMessage };
            string json = JsonSerializer.Serialize(errorData);
            await SendJsonAsync(socket, json);
        }

        private async Task SendJsonAsync(WebSocket socket, string message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private string GetUserId(ClaimsPrincipal user)
        {
            return user.FindFirst("userId")?.Value;
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
        //public async Task BroadcastNonGroupMembersAsync(string message)
        //{
        //    byte[] outgoing = Encoding.UTF8.GetBytes(message);
        //    foreach (var kvp in _allConnections)
        //    {
        //        string userId = kvp.Key;
        //        WebSocket socket = kvp.Value;

        //        // 모든 그룹 중에 userId가 포함되어 있는지 검사
        //        bool isInGroup = _groups.Values.Any(group => group.ContainsKey(userId));
        //        if (!isInGroup && socket.State == WebSocketState.Open)
        //        {
        //            await socket.SendAsync(new ArraySegment<byte>(outgoing),
        //                WebSocketMessageType.Text,
        //                true,
        //                CancellationToken.None);
        //        }
        //    }
        //}
    }


    

    public class MessageTemplate
    {
        public string? command { get; set; }
        public string? group { get; set; }
        public string? message { get; set; }

        public override string ToString()
        {
            return $"command: {command}, group: {group}, message: {message}";
        }
    }
}
