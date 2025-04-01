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
        public async Task<IActionResult> BroadcastMessage([FromBody] BroadcastRequest request)
        {
            try
            {
                chatHandler.EnqueueMessage(new ChatMessage
                {
                    Group = request.Group,
                    Message = $"[서버가 보냄] {request.Message}"
                });
                return Ok("Message broadcast scheduled");
            }
            catch(Exception ex)
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
