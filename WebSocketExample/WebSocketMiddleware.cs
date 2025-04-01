namespace WebSocketExample
{
    public class WebSocketMiddleware
    {
        private readonly RequestDelegate next;

        public WebSocketMiddleware(RequestDelegate _next)
        {
            next = _next;
        }

        public async Task InvokeAsync(HttpContext context, ChatWebSocketHandler chatHandler)
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
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await chatHandler.HandleAsync(webSocket, context.User);
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
