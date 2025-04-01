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

        [HttpGet]
        [HttpPost("broadcast")]
        public async Task<IActionResult> BroadcastMessage([FromBody] ChatMessage request)
        {
            try
            {
                chatHandler.EnqueueMessage(new ChatMessage
                {
                    ///command = request.command,
                    group = request.group,
                    message = $"[서버가 보냄] {request.message}"
                });
                return Ok("Message broadcast scheduled");
            }
            catch(Exception ex)
            {
                throw;
            }
        }


    }

  
}
