namespace WebSocketExample
{
    public class ChatBackgroundService : BackgroundService
    {
        private readonly ChatWebSocketHandler _chatHandler;

        public ChatBackgroundService(ChatWebSocketHandler chatHandler)
        {
            _chatHandler = chatHandler;
        }

        /// <summary>
        /// 백그라운드로 돌아야하는 작업 ---> 주기적으로 모니터링하거나 로깅하는 작업 등등이 필요할경우.
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 메시지 큐 처리를 사용하지 않는 경우, 여기에 다른 주기적 작업이나 모니터링 로직을 추가할 수 있습니다.
            while (!stoppingToken.IsCancellationRequested)
            {
                // 예를 들어, 주기적으로 그룹 현황을 로그에 출력할 수 있음
                //await _chatHandler.BroadcastGroupAsync("group1", "서버가보냄");

                Console.WriteLine("Current group status:");
                Console.WriteLine(_chatHandler.GetGroupStatus());

                await Task.Delay(10000, stoppingToken); // 10초마다 실행
            }
        }
    }
}
