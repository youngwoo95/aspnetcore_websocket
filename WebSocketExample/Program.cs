
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace WebSocketExample
{
    // �׷쿡 ���Ե� Ŭ���̾�Ʈ ������ ������ Ŭ����
    public class ClientConnection
    {
        public WebSocket Socket { get; set; }
        public string UserId { get; set; }
    }

    // ä�� �޽��� ��
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


            // JWT ���� ����
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

            // ��Ʈ�ѷ�, Swagger �� �⺻ ���� ���
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            // WebSocket ���� ���� ���
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
            // ������ �ɼ�
            app.UseWebSockets(new WebSocketOptions
            {
                // Ŭ���̾�Ʈ���� �ֱ������� Ping �޽����� ���� ������ ����ִ��� Ȯ���մϴ�.
                KeepAliveInterval = TimeSpan.FromSeconds(120),
                // CORS ��å�� ���� Ư�� �������� ����� �� �ֽ��ϴ�.
                AllowedOrigins = { "https://example.com", "https://another.com" } // Cors
            });

            // Ŀ���� WebSocket �̵���� ���
            app.UseMiddleware<WebSocketMiddleware>();




            app.MapControllers();

            app.Run();


        }

       

    }
}
