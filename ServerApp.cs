using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;
using WebApp.Handlers;
using DotNetEnv;

using Deepgram;
using Microsoft.Extensions.Primitives;

namespace WebApp
{
    public class ServerApp
    {
        private readonly int port;

        public ServerApp(int port)
        {
            this.port = port;
        }

        public void Start()
        {
            // Initialize Library with default logging
            // Normal logging is "Info" level
            Library.Initialize();
            // OR very chatty logging
            // Library.Initialize(Deepgram.Logger.LogLevel.Verbose); // LogLevel.Default, LogLevel.Debug, LogLevel.Verbose

            var host = new WebHostBuilder()
                .UseKestrel()
                .ConfigureServices(services => services.AddSingleton(this))
                .Configure(app =>
                {
                    app.UseWebSockets(); // Ensure WebSockets are enabled
                    var webSocketOptions = new WebSocketOptions()
                    {
                        KeepAliveInterval = TimeSpan.MaxValue,
                    };
                    app.UseWebSockets(webSocketOptions);

                    // handle websocket requests
                    app.Use(async (context, next) =>
                    {
                        Console.WriteLine($"----------- {context.Request.Path}");
                        if (context.Request.Path == "/ws")
                        {
                            if (context.WebSockets.IsWebSocketRequest)
                            {
                                // get the form data
                                var qs = context.Request.Query;
                                string model = qs.TryGetValue("model", out StringValues modelValues) ? modelValues.ToString() : string.Empty;

                                WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                                await new ApiHandler(webSocket, model).HandleApiRequest(context);
                            }
                            else
                            {
                                context.Response.StatusCode = 400;
                            }
                        }
                        else
                        {
                            await next();
                        }
                    });

                    // all other requests
                    app.Run(new RequestHandler().HandleRequest);
                })
                .UseUrls($"http://localhost:{port}/")
                .Build();

            host.Run();
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            DotNetEnv.Env.Load();
            string portString = Environment.GetEnvironmentVariable("port");
            if (portString == null)
            {
                portString = "3000";
            }
            int.TryParse(portString, out int port);
            ServerApp server = new ServerApp(port);
            server.Start();
            Console.WriteLine($"Server started on port {port}");
        }
    }
}
