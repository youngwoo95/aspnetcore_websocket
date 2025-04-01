using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace WebSocketExample.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ServerSentController : ControllerBase
    {
        private readonly ChatWebSocketHandler chatHandler;

        public ServerSentController(ChatWebSocketHandler _chathandler)
        {
            this.chatHandler = _chathandler;
        }

        [HttpPost]
        [Route("broadcast")]
        public async Task<IActionResult> BroadcastMessage([FromBody] ChatMessage request)
        {
            try
            {
                // 예시 1: 특정 그룹에 메시지 브로드캐스트
                 await chatHandler.BroadcastGroupAsync(request.group, $"[서버가 보냄] {request.message}");

                // 예시 2: 그룹에 가입하지 않은 모든 클라이언트에게 브로드캐스트
                //await chatHandler.BroadcastNonGroupMembersAsync($"[서버가 보냄] {request.message}");
                return Ok("Message broadcast scheduled");
            }
            catch(Exception ex)
            {
                throw;
            }
        }


    }

  
}
