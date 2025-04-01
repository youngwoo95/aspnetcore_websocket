namespace WebSocketExample
{
    public class ChatBackgroundService : BackgroundService
    {
        private readonly ChatWebSocketHandler _chatHandler;

        public ChatBackgroundService(ChatWebSocketHandler chatHandler)
        {
            _chatHandler = chatHandler;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _chatHandler.ProcessMessageQueueAsync(stoppingToken);
        }
    }
}
