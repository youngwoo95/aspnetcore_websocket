using System;

namespace WebSocketExample
{
    public class WebSocketMiddleware
    {
        private readonly RequestDelegate next;

        public WebSocketMiddleware(RequestDelegate _next)
        {
            next = _next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // "/ws" 경로에 해당하는 요청만 처리
            if (context.Request.Path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
            {
                // JWT 토큰 없으면 401 리턴
                if (!context.User.Identity?.IsAuthenticated ?? true)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                if (context.WebSockets.IsWebSocketRequest)
                {
                    var requestedProtocols = context.Request.Headers["Sec-WebSocket-Protocol"].ToString();
                    string selectedSubProtocol = null;

                    // 클라이언트가 "stmop" 서브프로토콜을 요청했다면.
                    if(requestedProtocols.Contains("stomp", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedSubProtocol = "stomp";
                    }
                    

                    // 핸드셰이크 처리 - 선택한 프로토콜이 있다면 전달
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync(selectedSubProtocol);

                    IChatWebSocketHandler handler;
                    if(selectedSubProtocol == "stomp")
                    {
                        handler = context.RequestServices.GetRequiredService<ChatStompWebSocketHandler>();
                    }
                    else
                    {
                        handler = context.RequestServices.GetRequiredService<ChatJsonWebSocketHandler>();
                    }

                    await handler.HandleAsync(webSocket, context.User);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            }
            else
            {
                // "/ws" 경로가 아니면 다음 미들웨어로 넘김
                await next(context);
            }
        }

    }
}
