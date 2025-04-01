
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
        public string Group { get; init; }
        public string Message { get; init; }
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
            
            // WebSocket ��������Ʈ ���
            app.Map("/ws", async context =>
            {
                // JWT ��ū ������ 401 
                if (!context.User.Identity?.IsAuthenticated ?? true)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var handler = app.Services.GetRequiredService<ChatWebSocketHandler>();
                    await handler.HandleAsync(webSocket, context.User);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            });



            app.MapControllers();

            app.Run();


        }

       

    }
}
