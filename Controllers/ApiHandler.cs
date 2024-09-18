using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using DotNetEnv;
using WebApp.Handlers;

using Deepgram;
using Deepgram.Models.Authenticate.v1;
using Deepgram.Models.Speak.v1.WebSocket;
using System.Net.WebSockets;
using System.Threading;
using Microsoft.AspNetCore.Connections.Features;
using Deepgram.Clients.Interfaces.v1;
using System.Timers;
using System.Reflection.PortableExecutable;
using System.Runtime.Intrinsics.X86;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Deepgram.Models.Manage.v1;

namespace WebApp.Handlers
{
public class ApiHandler
    {
        private string _model;
        private ISpeakWebSocketClient? _speakClient = null;

        private WebSocket _wsConn;
        private CancellationTokenSource _cancellationTokenSource;

        public ApiHandler(WebSocket wsConn, string model)
        {
            if (string.IsNullOrEmpty(model))
            {
                model = "aura-asteria-en";
            }

            _model = model;
            _wsConn = wsConn;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task HandleApiRequest(HttpContext context)
        {
            DateTime lastTime = DateTime.Now.AddSeconds(-5);

            // use the client factory with a API Key set with the "DEEPGRAM_API_KEY" environment variable
            if (_speakClient == null)
            {
                Console.WriteLine("Creating Deepgram Speak WebSocket Client...");
                _speakClient = ClientFactory.CreateSpeakWebSocketClient();

                // Subscribe to the EventResponseReceived event
                _speakClient.Subscribe(new EventHandler<OpenResponse>(async (sender, e) =>
                {
                    Console.WriteLine($"\n\n----> {e.Type} received");
                    await _wsConn.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(e.ToString())), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                }));
                _speakClient.Subscribe(new EventHandler<AudioResponse>(async (sender, e) =>
                {
                    if (e.Stream == null)
                    {
                        Console.WriteLine($"----> {e.Type} received, but Stream value is null");
                        return;
                    }

                    if ((DateTime.Now - lastTime).TotalSeconds > 3)
                    {
                        Console.WriteLine("------------ [Binary Data] Attach header.\n");

                        // Add a wav audio container header to the file if you want to play the audio
                        // using the AudioContext or media player like VLC, Media Player, or Apple Music
                        // Without this header in the Chrome browser case, the audio will not play.
                        byte[] header = new byte[]
                        {
                            0x52, 0x49, 0x46, 0x46, // "RIFF"
                            0x00, 0x00, 0x00, 0x00, // Placeholder for file size
                            0x57, 0x41, 0x56, 0x45, // "WAVE"
                            0x66, 0x6d, 0x74, 0x20, // "fmt "
                            0x10, 0x00, 0x00, 0x00, // Chunk size (16)
                            0x01, 0x00, // Audio format (1 for PCM)
                            0x01, 0x00, // Number of channels (1)
                            0x80, 0xbb, 0x00, 0x00, // Sample rate (48000)
                            0x00, 0xee, 0x02, 0x00, // Byte rate (48000 * 2)
                            0x02, 0x00, // Block align (2)
                            0x10, 0x00, // Bits per sample (16)
                            0x64, 0x61, 0x74, 0x61, // "data"
                            0x00, 0x00, 0x00, 0x00, // Placeholder for data size
                        };

                        await _wsConn.SendAsync(new ArraySegment<byte>(header), WebSocketMessageType.Binary, true, _cancellationTokenSource.Token);
                        lastTime = DateTime.Now;
                    }

                    Console.WriteLine($"----> {e.Type} received");
                    await _wsConn.SendAsync(new ArraySegment<byte>(e.Stream.ToArray()), WebSocketMessageType.Binary, true, _cancellationTokenSource.Token);
                }));
                _speakClient.Subscribe(new EventHandler<FlushedResponse>(async (sender, e) =>
                {
                    Console.WriteLine($"----> {e.Type} received");
                    await _wsConn.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(e.ToString())), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);

                }));
                _speakClient.Subscribe(new EventHandler<CloseResponse>(async (sender, e) =>
                {
                    Console.WriteLine($"----> {e.Type} received");

                    if (_wsConn.State == WebSocketState.Open)
                        await _wsConn.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(e.ToString())), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);

                }));
                _speakClient.Subscribe(new EventHandler<ErrorResponse>(async (sender, e) =>
                {
                    Console.WriteLine($"----> {e.Type} received. Error: {e.Message}");

                    if (_wsConn.State == WebSocketState.Open)
                        await _wsConn.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(e.ToString())), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                }));

                if (_model == null)
                {
                    _model = "aura-asteria-en";
                }

                // Start the connection
                var speakSchema = new SpeakSchema()
                {
                    Model = _model,
                    Encoding = "linear16",
                    SampleRate = "48000",
                };

                await _speakClient.Connect(speakSchema);
            }

            var buffer = new ArraySegment<byte>(new byte[4096]);
            WebSocketReceiveResult result;

            while (_wsConn.State == WebSocketState.Open)
            {
                using (var ms = new MemoryStream())
                {
                    do
                    {
                        // get the result of the receive operation
                        result = await _wsConn.ReceiveAsync(buffer, _cancellationTokenSource.Token);

                        ms.Write(
                            buffer.Array ?? throw new InvalidOperationException("buffer cannot be null"),
                            buffer.Offset,
                            result.Count
                            );
                    } while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Close)
                    {
                        ProcessDataReceived(result, ms);
                    }
                }
            }

            // close the DG speak websocket
            _speakClient.Close();
            _speakClient = null;
        }

        private void ProcessDataReceived(WebSocketReceiveResult result, MemoryStream ms)
        {
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var response = Encoding.UTF8.GetString(ms.ToArray());
                if (response == null)
                {
                    Console.WriteLine("Invalid - Received empty websocket message");
                    return;
                }

                var data = JsonDocument.Parse(response);
                var textResponse = data.Deserialize<TextMessage>();

                if (textResponse == null || string.IsNullOrEmpty(textResponse.Text))
                {
                    Console.WriteLine("Invalid - Received empty text message");
                    return;
                }

                Console.WriteLine($"Received text message:\n{textResponse.Text}");
                _speakClient.SpeakWithText(textResponse.Text);
                _speakClient.Flush();
            }
            else if (result.MessageType == WebSocketMessageType.Binary)
            {
                // we should not be receiving binary data
                Console.WriteLine("Invalid - Received binary data");
            }
        }

        public class TextMessage
        {
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            [JsonPropertyName("text")]
            public string? Text { get; set; }
        }
    }
}