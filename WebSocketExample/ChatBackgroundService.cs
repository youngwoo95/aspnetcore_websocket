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
            // 메시지 큐 처리를 사용하지 않는 경우, 여기에 다른 주기적 작업이나 모니터링 로직을 추가할 수 있습니다.
            while (!stoppingToken.IsCancellationRequested)
            {
                // 예를 들어, 주기적으로 그룹 현황을 로그에 출력할 수 있음
                Console.WriteLine("Current group status:");
                Console.WriteLine(_chatHandler.GetGroupStatus());

                await Task.Delay(10000, stoppingToken); // 10초마다 실행
            }
        }
    }
}
