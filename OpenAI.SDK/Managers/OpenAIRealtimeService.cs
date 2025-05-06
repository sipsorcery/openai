using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RealtimeModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
#if !NET6_0_OR_GREATER
using System.Text.Json.Serialization;
#endif

namespace Betalgo.Ranul.OpenAI.Managers;

/// <summary>
/// Service interface for interacting with the OpenAI Realtime API over WebSocket.
/// Provides real-time communication capabilities for text and audio interactions.
/// </summary>
public interface IOpenAIRealtimeService : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether the service is currently connected to the OpenAI Realtime API.
    /// </summary>
    /// <value>True if connected to the WebSocket server, otherwise false.</value>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the client events interface for sending events to the OpenAI Realtime API.
    /// These events include session updates, audio buffer operations, conversation management, and response generation.
    /// </summary>
    IOpenAIRealtimeServiceClientEvents ClientEvents { get; }

    /// <summary>
    /// Gets the server events interface for receiving events from the OpenAI Realtime API.
    /// These events include status updates, content streaming, and error notifications.
    /// </summary>
    IOpenAIRealtimeServiceServerEvents ServerEvents { get; }

    /// <summary>
    /// Establishes a WebSocket connection to the OpenAI Realtime API.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token to cancel the connection attempt.</param>
    /// <returns>A task that represents the asynchronous connection operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when already connected or when connection fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully closes the WebSocket connection to the OpenAI Realtime API.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token to cancel the disconnection attempt.</param>
    /// <returns>A task that represents the asynchronous disconnection operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SendEvent<T>(T message, CancellationToken cancellationToken) where T : class;
}

/// <summary>
/// Base class for OpenAI Realtime implementations.
/// </summary>
public partial class OpenAIRealtimeService
{
    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with dependency injection support.
    /// </summary>
    /// <param name="settings">OpenAI API configuration options.</param>
    /// <param name="logger">Logger instance for service diagnostics.</param>
    public OpenAIRealtimeService(IOptions<OpenAIOptions> settings, ILogger<OpenAIRealtimeService> logger)
    {
        _openAIOptions = settings.Value;
        _logger = logger;
        _clientEvents = new(this);
        _serverEvents = new();
    }

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with minimal configuration.
    /// </summary>
    /// <param name="options">OpenAI API configuration options.</param>
    public OpenAIRealtimeService(OpenAIOptions options) : this(Options.Create(options), NullLogger<OpenAIRealtimeService>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with logging support.
    /// </summary>
    /// <param name="options">OpenAI API configuration options.</param>
    /// <param name="logger">Logger instance for service diagnostics.</param>
    public OpenAIRealtimeService(OpenAIOptions options, ILogger<OpenAIRealtimeService> logger) : this(Options.Create(options), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the OpenAIRealtimeService with just an API key.
    /// </summary>
    /// <param name="apiKey">OpenAI API key for authentication.</param>
    public OpenAIRealtimeService(string apiKey) : this(new OpenAIOptions { ApiKey = apiKey })
    {
    }

    /// <summary>
    /// Creates a new OpenAIRealtimeService builder instance using an API key.
    /// </summary>
    /// <param name="apiKey">OpenAI API key for authentication.</param>
    /// <returns>A builder instance for configuring the service.</returns>
    public static OpenAIRealtimeServiceBuilder Create(string apiKey)
    {
        return new(apiKey);
    }

    /// <summary>
    /// Creates a new OpenAIRealtimeService builder instance using configuration options.
    /// </summary>
    /// <param name="options">OpenAI API configuration options.</param>
    /// <returns>A builder instance for configuring the service.</returns>
    public static OpenAIRealtimeServiceBuilder Create(OpenAIOptions options)
    {
        return new(options);
    }

    public virtual bool IsConnected => false;

    public virtual Task SendEvent<T>(T message, CancellationToken cancellationToken) where T : class
    {
        return Task.CompletedTask;
    }
}


public partial class OpenAIRealtimeService
{
#if !NET6_0_OR_GREATER
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Disallow, // Disallow comments
        AllowTrailingCommas = false,                        // Disallow trailing commas
        PropertyNameCaseInsensitive = false,                // Case-sensitive property names
        WriteIndented = false                               // Disable indentation for compact output
    };
#endif

    protected readonly ILogger<OpenAIRealtimeService> _logger;
    protected readonly OpenAIOptions _openAIOptions;
    private readonly ClientEventsImplementation _clientEvents;
    protected readonly OpenAIRealtimeServiceServerEvents _serverEvents;

    protected static readonly Dictionary<string, Action<OpenAIRealtimeService, JsonElement>> EventHandlers = InitializeEventHandlers();

    private static Dictionary<string, Action<OpenAIRealtimeService, JsonElement>> InitializeEventHandlers()
    {
        return new()
        {
            // Error
            { RealtimeEventTypes.Server.Error, (service, element) => service._serverEvents.RaiseOnError(service, service.DeserializeEvent<ErrorEvent>(element)) },

            // Session events
            { RealtimeEventTypes.Server.Session.Created, (service, element) => service._serverEvents.SessionImpl.RaiseOnCreated(service, service.DeserializeEvent<SessionEvent>(element)) },
            { RealtimeEventTypes.Server.Session.Updated, (service, element) => service._serverEvents.SessionImpl.RaiseOnUpdated(service, service.DeserializeEvent<SessionEvent>(element)) },

            // Conversation events
            { RealtimeEventTypes.Server.Conversation.Created, (service, element) => service._serverEvents.ConversationImpl.RaiseOnCreated(service, service.DeserializeEvent<ConversationCreatedEvent>(element)) },
            { RealtimeEventTypes.Server.Conversation.Item.Created, (service, element) => service._serverEvents.ConversationImpl.ItemImpl.RaiseOnCreated(service, service.DeserializeEvent<ConversationItemCreatedEvent>(element)) },
            { RealtimeEventTypes.Server.Conversation.Item.Truncated, (service, element) => service._serverEvents.ConversationImpl.ItemImpl.RaiseOnTruncated(service, service.DeserializeEvent<ConversationItemTruncatedEvent>(element)) },
            { RealtimeEventTypes.Server.Conversation.Item.Deleted, (service, element) => service._serverEvents.ConversationImpl.ItemImpl.RaiseOnDeleted(service, service.DeserializeEvent<ConversationItemDeletedEvent>(element)) },
            {
                RealtimeEventTypes.Server.Conversation.Item.InputAudioTranscription.Completed,
                (service, element) => service._serverEvents.ConversationImpl.ItemImpl.InputAudioTranscriptionImpl.RaiseOnCompleted(service, service.DeserializeEvent<InputAudioTranscriptionCompletedEvent>(element))
            },
            {
                RealtimeEventTypes.Server.Conversation.Item.InputAudioTranscription.Failed,
                (service, element) => service._serverEvents.ConversationImpl.ItemImpl.InputAudioTranscriptionImpl.RaiseOnFailed(service, service.DeserializeEvent<InputAudioTranscriptionFailedEvent>(element))
            },

            // InputAudioBuffer events
            { RealtimeEventTypes.Server.InputAudioBuffer.Committed, (service, element) => service._serverEvents.InputAudioBufferImpl.RaiseOnCommitted(service, service.DeserializeEvent<AudioBufferCommittedEvent>(element)) },
            { RealtimeEventTypes.Server.InputAudioBuffer.Cleared, (service, element) => service._serverEvents.InputAudioBufferImpl.RaiseOnCleared(service, service.DeserializeEvent<AudioBufferClearedEvent>(element)) },
            { RealtimeEventTypes.Server.InputAudioBuffer.SpeechStarted, (service, element) => service._serverEvents.InputAudioBufferImpl.RaiseOnSpeechStarted(service, service.DeserializeEvent<AudioBufferSpeechStartedEvent>(element)) },
            { RealtimeEventTypes.Server.InputAudioBuffer.SpeechStopped, (service, element) => service._serverEvents.InputAudioBufferImpl.RaiseOnSpeechStopped(service, service.DeserializeEvent<AudioBufferSpeechStoppedEvent>(element)) },

            // Response events
            { RealtimeEventTypes.Server.Response.Created, (service, element) => service._serverEvents.ResponseImpl.RaiseOnCreated(service, service.DeserializeEvent<ResponseEvent>(element)) },
            { RealtimeEventTypes.Server.Response.Done, (service, element) => service._serverEvents.ResponseImpl.RaiseOnDone(service, service.DeserializeEvent<ResponseEvent>(element)) },

            // Response OutputItem events
            { RealtimeEventTypes.Server.Response.OutputItem.Added, (service, element) => service._serverEvents.ResponseImpl.OutputItemImpl.RaiseOnAdded(service, service.DeserializeEvent<ResponseOutputItemAddedEvent>(element)) },
            { RealtimeEventTypes.Server.Response.OutputItem.Done, (service, element) => service._serverEvents.ResponseImpl.OutputItemImpl.RaiseOnDone(service, service.DeserializeEvent<ResponseOutputItemDoneEvent>(element)) },

            // Response ContentPart events
            { RealtimeEventTypes.Server.Response.ContentPart.Added, (service, element) => service._serverEvents.ResponseImpl.ContentPartImpl.RaiseOnAdded(service, service.DeserializeEvent<ResponseContentPartEvent>(element)) },
            { RealtimeEventTypes.Server.Response.ContentPart.Done, (service, element) => service._serverEvents.ResponseImpl.ContentPartImpl.RaiseOnDone(service, service.DeserializeEvent<ResponseContentPartEvent>(element)) },

            // Response Text events
            { RealtimeEventTypes.Server.Response.Text.Delta, (service, element) => service._serverEvents.ResponseImpl.TextImpl.RaiseOnDelta(service, service.DeserializeEvent<TextStreamEvent>(element)) },
            { RealtimeEventTypes.Server.Response.Text.Done, (service, element) => service._serverEvents.ResponseImpl.TextImpl.RaiseOnDone(service, service.DeserializeEvent<TextStreamEvent>(element)) },

            // Response AudioTranscript events
            { RealtimeEventTypes.Server.Response.AudioTranscript.Delta, (service, element) => service._serverEvents.ResponseImpl.AudioTranscriptImpl.RaiseOnDelta(service, service.DeserializeEvent<AudioTranscriptStreamEvent>(element)) },
            { RealtimeEventTypes.Server.Response.AudioTranscript.Done, (service, element) => service._serverEvents.ResponseImpl.AudioTranscriptImpl.RaiseOnDone(service, service.DeserializeEvent<AudioTranscriptStreamEvent>(element)) },

            // Response Audio events
            { RealtimeEventTypes.Server.Response.Audio.Delta, (service, element) => service._serverEvents.ResponseImpl.AudioImpl.RaiseOnDelta(service, service.DeserializeEvent<AudioStreamEvent>(element)) },
            { RealtimeEventTypes.Server.Response.Audio.Done, (service, element) => service._serverEvents.ResponseImpl.AudioImpl.RaiseOnDone(service, service.DeserializeEvent<AudioStreamEvent>(element)) },

            // Response FunctionCallArguments events
            {
                RealtimeEventTypes.Server.Response.FunctionCallArguments.Delta,
                (service, element) => service._serverEvents.ResponseImpl.FunctionCallArgumentsImpl.RaiseOnDelta(service, service.DeserializeEvent<FunctionCallStreamEvent>(element))
            },
            {
                RealtimeEventTypes.Server.Response.FunctionCallArguments.Done,
                (service, element) => service._serverEvents.ResponseImpl.FunctionCallArgumentsImpl.RaiseOnDone(service, service.DeserializeEvent<FunctionCallStreamEvent>(element))
            },

            // RateLimits events
            { RealtimeEventTypes.Server.RateLimits.Updated, (service, element) => service._serverEvents.RateLimitsImpl.RaiseOnUpdated(service, service.DeserializeEvent<RateLimitsEvent>(element)) }
        };
    }

    private T DeserializeEvent<T>(JsonElement jsonElement) where T : class
    {
#if NET6_0_OR_GREATER
        var typeInfo = RealtimeServiceJsonContext.Default.GetTypeInfo(typeof(T));
        return typeInfo != null ? jsonElement.Deserialize((JsonTypeInfo<T>)typeInfo)! : jsonElement.Deserialize<T>()!;
#else
        // For earlier versions, JsonSerializer does not support deserializing directly from JsonElement.
        // We need to use GetRawText(), which does incur some overhead but avoids reparsing the entire message.
        return JsonSerializer.Deserialize<T>(jsonElement.GetRawText(), JsonOptions)!;
#endif
    }
}
