using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace WebSocketExample.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ServerSentController : ControllerBase
    {
        // STOMP 방식
        private readonly ChatStompWebSocketHandler StompSocketHandler;

        // JSON 방식
        private readonly ChatJsonWebSocketHandler JsonSocketHandler;

        public ServerSentController(ChatJsonWebSocketHandler chatjsonHandler, ChatStompWebSocketHandler chatstompHandler)
        {
            this.JsonSocketHandler = chatjsonHandler;
            this.StompSocketHandler = chatstompHandler;
        }

        [HttpPost]
        [Route("broadcast")]
        public async Task<IActionResult> BroadcastMessage([FromBody] MessageTemplate request)
        {
            try
            {
                // 특정 그룹에 메시지 브로드캐스트
                 await JsonSocketHandler.BroadcastGroupAsync(request.group, $"[서버가 보냄] {request.message}");

                // 그룹에 가입하지 않은 모든 클라이언트에게 브로드캐스트
                //await chatHandler.BroadcastNonGroupMembersAsync($"[서버가 보냄] {request.message}");
                return Ok("Message broadcast scheduled");
            }
            catch(Exception ex)
            {
                throw;
            }
        }

        [HttpPost]
        [Route("broadcast2")]
        public async Task<IActionResult> BroadcastMessage2([FromBody] BroadcastRequest request)
        {
            try
            {
                await StompSocketHandler.BroadcastGroupAsync(request.Group, request.Message);
                return Ok("Message BroadCast");
            }catch(Exception ex)
            {
                throw;
            }
        }

    }

    public class BroadcastRequest
    {
        public string Group { get; set; }
        public string Message { get; set; }
    }

  
}
