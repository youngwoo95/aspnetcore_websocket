
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace WebSocketExample
{
    // 그룹에 가입된 클라이언트 정보를 보관할 클래스
    public class ClientConnection
    {
        public WebSocket Socket { get; set; }
        public string UserId { get; set; }
    }

    // 채팅 메시지 모델
    public record ChatMessage
    {
        public string? command { get; set; }
        public string? group { get; init; }
        public string? message { get; set; }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);


            // JWT 인증 설정
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["JWT:authSigningKey"]!)),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidIssuer = builder.Configuration["JWT:Issuer"],
                    ValidAudience = builder.Configuration["JWT:Audience"],
                    RoleClaimType = "Role",
                    ClockSkew = TimeSpan.Zero,
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var authHeader = context.Request.Headers["Authorization"].ToString();
                        if (!string.IsNullOrEmpty(authHeader) && !authHeader.StartsWith("Bearer "))
                        {
                            context.Token = authHeader;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

            // 컨트롤러, Swagger 등 기본 서비스 등록
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            // WebSocket 관련 서비스 등록
            builder.Services.AddSingleton<ChatWebSocketHandler>();
            builder.Services.AddHostedService<ChatBackgroundService>();

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();
            app.UseAuthentication();
            app.UseAuthorization();
            // 웹소켓 옵션
            app.UseWebSockets(new WebSocketOptions
            {
                // 클라이언트에게 주기적으로 Ping 메시지를 보내 연결이 살아있는지 확인합니다.
                KeepAliveInterval = TimeSpan.FromSeconds(120),
                // CORS 정책에 따라 특정 오리진만 허용할 수 있습니다.
                AllowedOrigins = { "https://example.com", "https://another.com" } // Cors
            });

            // 커스텀 WebSocket 미들웨어 등록
            app.UseMiddleware<WebSocketMiddleware>();




            app.MapControllers();

            app.Run();


        }

       

    }
}
