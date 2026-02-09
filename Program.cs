/**
 * C# Live Text-to-Speech Starter - Backend Server
 *
 * A WebSocket proxy server that transparently forwards text and audio
 * messages between browser clients and Deepgram's Live Text-to-Speech API.
 *
 * Key Features:
 * - WebSocket proxy: /api/live-text-to-speech -> wss://api.deepgram.com/v1/speak
 * - Bidirectional message forwarding (JSON text + binary audio)
 * - JWT session auth with page nonce (production only)
 * - Metadata endpoint: GET /api/metadata
 * - CORS enabled for frontend communication
 * - Graceful shutdown with connection tracking
 */

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Tomlyn;
using Tomlyn.Model;
using HttpResults = Microsoft.AspNetCore.Http.Results;

// ============================================================================
// ENVIRONMENT LOADING
// ============================================================================

DotNetEnv.Env.Load();

// ============================================================================
// CONFIGURATION
// ============================================================================

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8081;
var host = Environment.GetEnvironmentVariable("HOST") ?? "0.0.0.0";
var frontendPort = int.TryParse(Environment.GetEnvironmentVariable("FRONTEND_PORT"), out var fp) ? fp : 8080;

const string DeepgramTtsUrl = "wss://api.deepgram.com/v1/speak";

// ============================================================================
// SESSION AUTH - JWT tokens with page nonce for production security
// ============================================================================

var sessionSecretEnv = Environment.GetEnvironmentVariable("SESSION_SECRET");
var sessionSecret = sessionSecretEnv ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
var requireNonce = !string.IsNullOrEmpty(sessionSecretEnv);
var sessionSecretKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(sessionSecret));

var sessionNonces = new ConcurrentDictionary<string, long>();
const int NonceTtlSeconds = 5 * 60; // 5 minutes
const int JwtExpirySeconds = 3600; // 1 hour

string GenerateNonce()
{
    var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    sessionNonces[nonce] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + NonceTtlSeconds;
    return nonce;
}

bool ConsumeNonce(string nonce)
{
    if (!sessionNonces.TryRemove(nonce, out var expiry))
        return false;
    return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry;
}

// Cleanup expired nonces every 60 seconds
var nonceCleanupTimer = new Timer(_ =>
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    foreach (var kvp in sessionNonces)
    {
        if (now >= kvp.Value)
            sessionNonces.TryRemove(kvp.Key, out _);
    }
}, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

// Read frontend/dist/index.html template for nonce injection
string? indexHtmlTemplate = null;
try
{
    indexHtmlTemplate = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "frontend", "dist", "index.html"));
}
catch (FileNotFoundException) { }

string CreateSessionToken()
{
    var handler = new JwtSecurityTokenHandler();
    var descriptor = new SecurityTokenDescriptor
    {
        Expires = DateTime.UtcNow.AddSeconds(JwtExpirySeconds),
        SigningCredentials = new SigningCredentials(sessionSecretKey, SecurityAlgorithms.HmacSha256Signature),
    };
    var token = handler.CreateToken(descriptor);
    return handler.WriteToken(token);
}

bool ValidateSessionToken(string token)
{
    try
    {
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = sessionSecretKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero,
        }, out _);
        return true;
    }
    catch
    {
        return false;
    }
}

/// Validates JWT from WebSocket subprotocol: access_token.<jwt>
string? ValidateWsToken(string? protocolHeader)
{
    if (string.IsNullOrEmpty(protocolHeader)) return null;
    var protocols = protocolHeader.Split(',', StringSplitOptions.TrimEntries);
    var tokenProto = protocols.FirstOrDefault(p => p.StartsWith("access_token."));
    if (tokenProto == null) return null;
    var token = tokenProto["access_token.".Length..];
    return ValidateSessionToken(token) ? tokenProto : null;
}

// ============================================================================
// API KEY LOADING
// ============================================================================

static string LoadApiKey()
{
    var apiKey = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");

    if (string.IsNullOrEmpty(apiKey))
    {
        Console.Error.WriteLine("\n❌ ERROR: Deepgram API key not found!\n");
        Console.Error.WriteLine("Please set your API key using one of these methods:\n");
        Console.Error.WriteLine("1. Create a .env file (recommended):");
        Console.Error.WriteLine("   DEEPGRAM_API_KEY=your_api_key_here\n");
        Console.Error.WriteLine("2. Environment variable:");
        Console.Error.WriteLine("   export DEEPGRAM_API_KEY=your_api_key_here\n");
        Console.Error.WriteLine("Get your API key at: https://console.deepgram.com\n");
        Environment.Exit(1);
    }

    return apiKey;
}

var apiKey = LoadApiKey();

// ============================================================================
// SETUP
// ============================================================================

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{host}:{port}");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                $"http://localhost:{frontendPort}",
                $"http://127.0.0.1:{frontendPort}")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

// Track active connections for graceful shutdown
var activeConnections = new ConcurrentDictionary<string, WebSocket>();

// ============================================================================
// SESSION ROUTES - Auth endpoints (unprotected)
// ============================================================================

/// GET / — Serve index.html with injected session nonce (production only)
app.MapGet("/", () =>
{
    if (indexHtmlTemplate == null)
        return HttpResults.Text("Frontend not built. Run make build first.", statusCode: 404);

    // Cleanup expired nonces
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    foreach (var kvp in sessionNonces)
    {
        if (now >= kvp.Value)
            sessionNonces.TryRemove(kvp.Key, out _);
    }

    var nonce = GenerateNonce();
    var html = indexHtmlTemplate.Replace("</head>", $"<meta name=\"session-nonce\" content=\"{nonce}\">\n</head>");
    return HttpResults.Content(html, "text/html");
});

/// GET /api/session — Issues a JWT. In production, requires valid nonce.
app.MapGet("/api/session", (HttpRequest request) =>
{
    if (requireNonce)
    {
        var nonce = request.Headers["X-Session-Nonce"].FirstOrDefault();
        if (string.IsNullOrEmpty(nonce) || !ConsumeNonce(nonce))
        {
            return HttpResults.Json(new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, string>
                {
                    ["type"] = "AuthenticationError",
                    ["code"] = "INVALID_NONCE",
                    ["message"] = "Valid session nonce required. Please refresh the page.",
                }
            }, statusCode: 403);
        }
    }

    var token = CreateSessionToken();
    return HttpResults.Json(new Dictionary<string, string> { ["token"] = token });
});

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

/// Builds the Deepgram TTS WebSocket URL with query parameters forwarded from the client
static string BuildDeepgramUrl(string? queryString)
{
    var uri = new UriBuilder(DeepgramTtsUrl);
    var query = System.Web.HttpUtility.ParseQueryString(queryString ?? "");

    var parameters = new Dictionary<string, string>
    {
        ["model"] = query["model"] ?? "aura-asteria-en",
        ["encoding"] = query["encoding"] ?? "linear16",
        ["sample_rate"] = query["sample_rate"] ?? "48000",
        ["container"] = query["container"] ?? "none",
    };

    var qs = System.Web.HttpUtility.ParseQueryString("");
    foreach (var kvp in parameters)
        qs[kvp.Key] = kvp.Value;
    uri.Query = qs.ToString();

    return uri.ToString();
}

/// Forwards messages from one WebSocket to another
static async Task ForwardMessages(WebSocket source, WebSocket destination, string direction, CancellationToken ct)
{
    var buffer = new byte[8192];
    var messageCount = 0;

    try
    {
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Propagate close to destination
                if (destination.State == WebSocketState.Open)
                {
                    await destination.CloseAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription ?? "Connection closed",
                        ct);
                }
                break;
            }

            messageCount++;
            var logInterval = direction == "client→deepgram" ? 100 : 10;
            var isBinary = result.MessageType == WebSocketMessageType.Binary;
            if (messageCount % logInterval == 0 || !isBinary)
            {
                Console.WriteLine($"  {(direction == "client→deepgram" ? "→" : "←")} {direction} #{messageCount} (binary: {isBinary}, size: {result.Count})");
            }

            if (destination.State == WebSocketState.Open)
            {
                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    ct);
            }
        }
    }
    catch (WebSocketException ex)
    {
        Console.Error.WriteLine($"  WebSocket error in {direction}: {ex.Message}");
    }
    catch (OperationCanceledException)
    {
        // Shutdown requested
    }
}

/// Handles a single WebSocket proxy session between client and Deepgram TTS
async Task HandleTtsStream(WebSocket clientWs, string? queryString, string apiKey, CancellationToken appCt)
{
    var connectionId = Guid.NewGuid().ToString("N")[..8];
    activeConnections[connectionId] = clientWs;
    Console.WriteLine($"[{connectionId}] Client connected to /api/live-text-to-speech");

    var deepgramUrl = BuildDeepgramUrl(queryString);
    Console.WriteLine($"[{connectionId}] Connecting to Deepgram: {deepgramUrl}");

    using var deepgramWs = new ClientWebSocket();
    deepgramWs.Options.SetRequestHeader("Authorization", $"Token {apiKey}");

    try
    {
        await deepgramWs.ConnectAsync(new Uri(deepgramUrl), appCt);
        Console.WriteLine($"[{connectionId}] ✓ Connected to Deepgram TTS API");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(appCt);

        var clientToDeepgram = ForwardMessages(clientWs, deepgramWs, "client→deepgram", cts.Token);
        var deepgramToClient = ForwardMessages(deepgramWs, clientWs, "deepgram→client", cts.Token);

        // Wait for either direction to complete
        await Task.WhenAny(clientToDeepgram, deepgramToClient);
        cts.Cancel();

        // Allow the other task to finish
        try { await Task.WhenAll(clientToDeepgram, deepgramToClient); }
        catch (OperationCanceledException) { }
    }
    catch (WebSocketException ex)
    {
        Console.Error.WriteLine($"[{connectionId}] Deepgram connection error: {ex.Message}");
        if (clientWs.State == WebSocketState.Open)
        {
            await clientWs.CloseAsync(
                WebSocketCloseStatus.InternalServerError,
                "Deepgram connection error",
                CancellationToken.None);
        }
    }
    catch (OperationCanceledException)
    {
        // App shutdown
    }
    finally
    {
        // Close connections if still open
        if (clientWs.State == WebSocketState.Open)
        {
            try
            {
                await clientWs.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection ended",
                    CancellationToken.None);
            }
            catch { }
        }
        if (deepgramWs.State == WebSocketState.Open)
        {
            try
            {
                await deepgramWs.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection ended",
                    CancellationToken.None);
            }
            catch { }
        }

        activeConnections.TryRemove(connectionId, out _);
        Console.WriteLine($"[{connectionId}] Connection closed ({activeConnections.Count} active)");
    }
}

// ============================================================================
// WEBSOCKET ENDPOINT
// ============================================================================

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/api/live-text-to-speech" && context.WebSockets.IsWebSocketRequest)
    {
        // Validate JWT from WebSocket subprotocol
        var protocolHeader = context.Request.Headers["Sec-WebSocket-Protocol"].FirstOrDefault();
        var validProto = ValidateWsToken(protocolHeader);
        if (validProto == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var clientWs = await context.WebSockets.AcceptWebSocketAsync(validProto);
        await HandleTtsStream(clientWs, context.Request.QueryString.Value, apiKey, context.RequestAborted);
    }
    else
    {
        await next(context);
    }
});

// ============================================================================
// API ROUTES
// ============================================================================

/// GET /api/metadata
///
/// Returns metadata about this starter application from deepgram.toml
app.MapGet("/api/metadata", () =>
{
    try
    {
        var tomlPath = Path.Combine(Directory.GetCurrentDirectory(), "deepgram.toml");
        var tomlContent = File.ReadAllText(tomlPath);
        var tomlModel = Toml.ToModel(tomlContent);

        if (!tomlModel.ContainsKey("meta") || tomlModel["meta"] is not TomlTable metaTable)
        {
            return HttpResults.Json(new Dictionary<string, string>
            {
                ["error"] = "INTERNAL_SERVER_ERROR",
                ["message"] = "Missing [meta] section in deepgram.toml",
            }, statusCode: 500);
        }

        var meta = new Dictionary<string, object?>();
        foreach (var kvp in metaTable)
        {
            meta[kvp.Key] = kvp.Value;
        }

        return HttpResults.Json(meta);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading metadata: {ex}");
        return HttpResults.Json(new Dictionary<string, string>
        {
            ["error"] = "INTERNAL_SERVER_ERROR",
            ["message"] = "Failed to read metadata from deepgram.toml",
        }, statusCode: 500);
    }
});

// ============================================================================
// GRACEFUL SHUTDOWN
// ============================================================================

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine($"\nShutting down... Closing {activeConnections.Count} active connection(s)...");
    foreach (var kvp in activeConnections)
    {
        try
        {
            if (kvp.Value.State == WebSocketState.Open)
            {
                kvp.Value.CloseAsync(
                    WebSocketCloseStatus.EndpointUnavailable,
                    "Server shutting down",
                    CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error closing connection {kvp.Key}: {ex.Message}");
        }
    }
    Console.WriteLine("All connections closed.");
});

// ============================================================================
// SERVER START
// ============================================================================

var nonceStatus = requireNonce ? " (nonce required)" : "";
Console.WriteLine();
Console.WriteLine(new string('=', 70));
Console.WriteLine($"🚀 Backend API Server running at http://localhost:{port}");
Console.WriteLine($"📡 CORS enabled for http://localhost:{frontendPort}");
Console.WriteLine($"📡 GET  /api/session{nonceStatus}");
Console.WriteLine($"📡 WebSocket endpoint: ws://localhost:{port}/api/live-text-to-speech (auth required)");
Console.WriteLine($"📡 GET  /api/metadata");
Console.WriteLine($"\n💡 Frontend should be running on http://localhost:{frontendPort}");
Console.WriteLine(new string('=', 70));
Console.WriteLine();

app.Run();
