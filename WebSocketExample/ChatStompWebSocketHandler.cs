using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace WebSocketExample
{
    /// <summary>
    /// STOMP 전용 핸들러
    /// </summary>
    public class ChatStompWebSocketHandler : IChatWebSocketHandler
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ClientConnection>> _groups = new();


        public async Task HandleAsync(WebSocket webSocket, ClaimsPrincipal user)
        {
            var userId = GetUserId(user);
            if (string.IsNullOrEmpty(userId))
            {
                // userId가 없으면 연결 종료
                await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "User id not found", CancellationToken.None);
                return;
            }


            // STOMP 모드에서는 초기 CONNECT, SUBSCRIBE, SEND 등 STOMP 프레임 처리
            var buffer = new byte[1024 * 4];

            try
            {
                while(webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if(result.MessageType == WebSocketMessageType.Text)
                    {
                        var frame = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await ProcessStompFrameAsync(webSocket, frame, user);
                    }
                    else if(result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                }
            }
            catch(WebSocketException ex)
            {
                Console.WriteLine("WebSocket exception:" + ex.Message);
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
        /// <param name="user"></param>
        private void RemoveConnectionFromAllGroups(WebSocket socket, ClaimsPrincipal user)
        {
            var userId = GetUserId(user);
            foreach(var groupKey in _groups.Keys.ToList())
            {
                if(_groups.TryGetValue(groupKey, out var group))
                {
                    group.TryRemove(userId, out _);
                    if(group.IsEmpty)
                    {
                        _groups.TryRemove(groupKey, out _);
                    }
                }
            }
            var Result = GetGroupStatus();
            if (String.IsNullOrWhiteSpace(Result))
            {
                Console.WriteLine("연결중인 User가 없습니다.");
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


        private async Task ProcessStompFrameAsync(WebSocket webSocket, string frame, ClaimsPrincipal user)
        {
            var lines = frame.Split(new[] { "\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                await SendErrorAsync(webSocket, "Empty STOMP frame");
                return;
            }

            var command = lines[0].Trim().ToUpper();
            switch (command)
            {
                case "CONNECT":
                    await HandleStompConnectAsync(webSocket);
                    break;
                case "SUBSCRIBE":
                    await HandleStompSubscribeAsync(webSocket, lines, user);
                    break;
                case "SEND":
                    await HandleStompReceiveAsync(webSocket, lines);
                    break;
                case "DISCONNECT":
                    await HandleStompDisconnectAsync(webSocket);
                    break;
                default:
                    await SendErrorAsync(webSocket, "Unknown STOMP command: " + command);
                    break;
            }
        }

        private async Task HandleStompConnectAsync(WebSocket webSocket)
        {
            var response = "CONNECTED\nversion:1.2\n\n\0";
            await SendAsync(webSocket, response);
        }

        private async Task HandleStompSubscribeAsync(WebSocket webSocket, string[] lines, ClaimsPrincipal user)
        {
            string destination = string.Empty;
            string subscriptionId = string.Empty;

            foreach(var line in lines)
            {
                if (line.StartsWith("destination:", StringComparison.OrdinalIgnoreCase))
                    destination = line.Substring("destination:".Length).Trim();
                if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                    subscriptionId = line.Substring("id:".Length).Trim();
            }

            if(string.IsNullOrEmpty(destination) || string.IsNullOrEmpty(subscriptionId))
            {
                await SendErrorAsync(webSocket, "SUBSCRIBE frame missing destination or id");
                return;
            }

            var group = _groups.GetOrAdd(destination, _ => new ConcurrentDictionary<string, ClientConnection>());
            var userId = GetUserId(user);
            if (!string.IsNullOrEmpty(userId))
            {
                group[userId] = new ClientConnection { Socket = webSocket, UserId = userId };
            }

            var receiptFrame = $"RECEIPT\nreceipt-id:{subscriptionId}\n\n\0";
            await SendAsync(webSocket, receiptFrame);
        }


        private async Task HandleStompReceiveAsync(WebSocket webSocket, string[] lines)
        {
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
                    destination = lines[i].Substring("destination:".Length).Trim();
            }

            if (string.IsNullOrEmpty(destination))
            {
                await SendErrorAsync(webSocket, "SEND frame missing destination");
                return;
            }

            var bodyBuilder = new StringBuilder();
            for (int i = bodyIndex; i < lines.Length; i++)
            {
                bodyBuilder.AppendLine(lines[i].Replace("\0", ""));
            }

            var messageBody = bodyBuilder.ToString().TrimEnd();
            Console.WriteLine($"STOMP SEND received. Destination: {destination}, message: {messageBody}");

            
            /*
            if(_groups.TryGetValue(destination, out var group))
            {
                foreach(var client in group.Values)
                {
                    var messageFrame = $"MESSAGE\ndestination:{destination}\n\n{messageBody}\0";
                    await SendAsync(client.Socket, messageFrame);
                }
            }
            */

        }

        public string GetJsonGroupStatus()
        {
            var sb = new StringBuilder();
            foreach (var group in _groups)
            {
                sb.AppendLine($"Group: {group.Value}");
                foreach (var userConnection in group.Value)
                {
                    sb.AppendLine($"   UserId: {userConnection.Key}");
                }
            }
            return sb.ToString();
        }



        private async Task HandleStompDisconnectAsync(WebSocket webSocket)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
        }

        private async Task SendErrorAsync(WebSocket socket, string errorMessage)
        {
            var errorData = new { error = errorMessage };
            string json = JsonSerializer.Serialize(errorData);
            await SendAsync(socket, json);
        }


        private async Task SendAsync(WebSocket socket, string message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task BroadcastGroupAsync(string destination, string message)
        {
            if(_groups.TryGetValue(destination, out var group))
            {
                // STOMP MESSAGE 프레임 구성 : 헤더와 본문, 마지막에 null 문자 포함
                string messageFrame = $"MESSAGE\ndestination:{destination}\n\n{message}\0";

                byte[] bytes = Encoding.UTF8.GetBytes(messageFrame);
                foreach (var client in group.Values)
                {
                    if (client.Socket.State == WebSocketState.Open)
                    {
                        await client.Socket.SendAsync(new ArraySegment<byte>(bytes),
                                                       WebSocketMessageType.Text,
                                                       true,
                                                       CancellationToken.None);
                    }
                }
            }
            else
            {
                Console.WriteLine($"Group '{destination}' does not exist.");
            }
        }
        


        private string GetUserId(ClaimsPrincipal user)
        {
            return user.FindFirst("userId")?.Value;
        }


    }

}
