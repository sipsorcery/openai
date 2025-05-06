using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels;
using Betalgo.Ranul.OpenAI.ObjectModels.RealtimeModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
#if !NET6_0_OR_GREATER
using System.Text.Json.Serialization;
#endif

namespace Betalgo.Ranul.OpenAI.Managers;

/// <summary>
/// WebSocket client wrapper for OpenAI Realtime API connections.
/// </summary>
public class OpenAIWebSocketClient
{
    public OpenAIWebSocketClient()
    { }

    /// <summary>
    /// Gets the underlying WebSocket client instance.
    /// </summary>
    public ClientWebSocket WebSocket { get; } = new();

    /// <summary>
    /// Configures the WebSocket client with custom settings.
    /// </summary>
    /// <param name="configure">Action to configure the WebSocket client.</param>
    public void ConfigureWebSocket(Action<ClientWebSocket> configure)
    {
        configure(WebSocket);
    }
}

/// <summary>
/// Main implementation of the OpenAI Realtime service providing WebSocket-based communication.
/// Supports real-time text and audio interactions with GPT-4 and related models.
/// </summary>
public class OpenAIRealtimeWebSocketService : OpenAIRealtimeService, IOpenAIRealtimeService
{
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveTask;
    private ClientWebSocket? _webSocket;
    private readonly object _webSocketLock = new();
    private readonly OpenAIWebSocketClient _webSocketClient;

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with dependency injection support.
    /// </summary>
    /// <param name="settings">OpenAI API configuration options.</param>
    /// <param name="logger">Logger instance for service diagnostics.</param>
    /// <param name="webSocketClient">WebSocket client for API communication.</param>
    public OpenAIRealtimeWebSocketService(IOptions<OpenAIOptions> settings, ILogger<OpenAIRealtimeService> logger, OpenAIWebSocketClient webSocketClient)
        : base(settings, logger)
    {
        ConfigureBaseWebSocket();
    }

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with minimal configuration.
    /// </summary>
    /// <param name="options">OpenAI API configuration options.</param>
    public OpenAIRealtimeWebSocketService(OpenAIOptions options) : base(Options.Create(options), NullLogger<OpenAIRealtimeService>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with logging support.
    /// </summary>
    /// <param name="options">OpenAI API configuration options.</param>
    /// <param name="logger">Logger instance for service diagnostics.</param>
    public OpenAIRealtimeWebSocketService(OpenAIOptions options, ILogger<OpenAIRealtimeService> logger) : base(Options.Create(options), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with custom WebSocket configuration.
    /// </summary>
    /// <param name="options">OpenAI API configuration options.</param>
    /// <param name="configureWebSocket">Optional action to configure the WebSocket client.</param>
    public OpenAIRealtimeWebSocketService(OpenAIOptions options, Action<ClientWebSocket>? configureWebSocket = null) : base(options)
    {
        if (configureWebSocket != null)
        {
            _webSocketClient.ConfigureWebSocket(configureWebSocket);
        }
    }

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with just an API key.
    /// </summary>
    /// <param name="apiKey">OpenAI API key for authentication.</param>
    public OpenAIRealtimeWebSocketService(string apiKey) : base(new OpenAIOptions { ApiKey = apiKey })
    {
    }

    /// <summary>
    /// Configures the base WebSocket connection with authentication headers.
    /// </summary>
    private void ConfigureBaseWebSocket()
    {
        var headers = new Dictionary<string, string>
        {
            { RealtimeConstants.Headers.Authorization, $"Bearer {_openAIOptions.ApiKey}" }
        };

        if (!string.IsNullOrEmpty(_openAIOptions?.Organization))
        {
            headers[RealtimeConstants.Headers.OpenAIOrganization] = _openAIOptions.Organization;
        }

        _webSocketClient.ConfigureWebSocket(ws => WebSocketConfigurationHelper.ConfigureWebSocket(ws, headers: headers));
    }

    /// <inheritdoc />
    public override bool IsConnected
    {
        get
        {
            lock (_webSocketLock)
            {
                return _webSocket?.State == WebSocketState.Open;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        linkedCts.Token.ThrowIfCancellationRequested();

        if (IsConnected)
            throw new InvalidOperationException("Already connected to Realtime API.");

        try
        {
            var webSocket = _webSocketClient.WebSocket;
            var url = new Uri($"{_openAIOptions.BaseRealTimeSocketUrl}?model={_openAIOptions.DefaultModelId ?? Models.Gpt_4o_realtime_preview_2024_10_01}");
            await webSocket.ConnectAsync(url, linkedCts.Token).ConfigureAwait(false);

            if (webSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException($"WebSocket connection failed. Current state: {webSocket.State}");
            }

            lock (_webSocketLock)
            {
                _webSocket = webSocket;
            }

            _logger.LogInformation("Successfully connected to Realtime API");
            _receiveTask = StartReceiving(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Connection canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Realtime API");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        linkedCts.Token.ThrowIfCancellationRequested();

        ClientWebSocket? webSocket;
        lock (_webSocketLock)
        {
            webSocket = _webSocket;
        }

        if (webSocket == null || webSocket.State != WebSocketState.Open) return;

        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Disconnection canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disconnection");
            throw;
        }
        finally
        {
            CleanupWebSocket();
        }
    }

    private Task HandleMessage(string message)
    {
        _serverEvents.RaiseOnAll(this, message);

        try
        {
            using var doc = JsonDocument.Parse(message);
            var rootElement = doc.RootElement;

            if (!rootElement.TryGetProperty("type", out var typeElement))
            {
                _logger.LogWarning("Received message without type");
                return Task.CompletedTask;
            }

            var type = typeElement.GetString();

            if (type != null && EventHandlers.TryGetValue(type, out var handler))
            {
                handler(this, rootElement);
            }
            else
            {
                _logger.LogWarning("Received unknown event type: {Type}", type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            var errorEvent = new ErrorEvent
            {
                Error = new()
                {
                    Type = "Ranul.OpenAI_processing_error",
                    MessageObject = ex.Message
                }
            };
            _serverEvents.RaiseOnError(this, errorEvent);
        }

        return Task.CompletedTask;
    }

    public override async Task SendEvent<T>(T message, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
#if NET6_0_OR_GREATER
                var json = JsonSerializer.Serialize(message, message.GetType(), RealtimeServiceJsonContext.Default);
#else
                var json = JsonSerializer.Serialize(message, JsonOptions);
#endif
                var buffer = Encoding.UTF8.GetBytes(json);

                ClientWebSocket? webSocket;
                lock (_webSocketLock)
                {
                    webSocket = _webSocket;
                }

                if (webSocket == null)
                    throw new InvalidOperationException("WebSocket is not initialized.");


                await webSocket.SendAsync(new(buffer), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message");
            throw;
        }
    }

    private async Task StartReceiving(CancellationToken cancellationToken)
    {
        ClientWebSocket? webSocket;
        lock (_webSocketLock)
        {
            webSocket = _webSocket;
        }

        if (webSocket == null)
            throw new InvalidOperationException("WebSocket is not initialized.");

        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1);
        var textBuffer = new StringBuilder();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await webSocket.ReceiveAsync(new(buffer), cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    _logger.LogWarning("WebSocket connection closed prematurely.");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await HandleCloseMessage().ConfigureAwait(false);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    textBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        await HandleMessage(textBuffer.ToString()).ConfigureAwait(false);
                        textBuffer.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation, do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in receive loop");
            _serverEvents.RaiseOnError(this, new()
            {
                Error = new()
                {
                    MessageObject = ex.Message,
                    Type = "Ranul.OpenAI_receive_error"
                }
            });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleCloseMessage()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    private void CleanupWebSocket()
    {
        ClientWebSocket? webSocket;
        lock (_webSocketLock)
        {
            webSocket = _webSocket;
            _webSocket = null;
        }

        if (webSocket != null)
        {
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    webSocket.Abort();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error aborting WebSocket");
                }
            }

            webSocket.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposeCts.Cancel();
        CleanupWebSocket();
        _sendLock.Dispose();
        _disposeCts.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();

        if (_receiveTask != null)
        {
            await _receiveTask.ConfigureAwait(false);
        }

        CleanupWebSocket();
        _sendLock.Dispose();
        _disposeCts.Dispose();
    }
}
