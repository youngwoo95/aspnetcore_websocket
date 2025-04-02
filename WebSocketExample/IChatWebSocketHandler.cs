using System.Net.WebSockets;
using System.Security.Claims;

namespace WebSocketExample
{
    public interface IChatWebSocketHandler
    {
        Task HandleAsync(WebSocket webSocket, ClaimsPrincipal user);
    }
}
