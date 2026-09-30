using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// A SignalR connection that stays up: reconnects on its own after a drop,
/// and starts over from scratch when the automatic reconnect gives up
/// (docs/OFFLINE_BEHAVIOR.md: a kiosk loses its link regularly).
/// </summary>
/// <remarks>
/// The token comes from <see cref="KioskApiClient"/> on every (re)connect, so
/// a token renewed in the meantime is the one presented.
/// </remarks>
public abstract class HubClientBase : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly KioskApiClient _api;
    private readonly KioskSettings _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HubConnection? _connection;
    private CancellationTokenSource? _running;

    protected HubClientBase(KioskApiClient api, IOptions<KioskSettings> settings, ILogger logger)
    {
        _api = api;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>The hub path, e.g. <c>hubs/kiosk</c>.</summary>
    protected abstract string Path { get; }

    /// <summary>For tests against an in-process server.</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; set; }

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>Connected again after a drop: whatever was missed has to be fetched.</summary>
    public event Func<Task>? Reconnected;

    /// <summary>Connects and keeps the connection up until <see cref="StopAsync"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_connection is not null)
            {
                return;
            }

            _running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connection = Build();
            Register(_connection);

            _connection.Reconnected += async _ =>
            {
                _logger.LogInformation("{Hub}: reconnected.", Path);
                await RaiseReconnectedAsync().ConfigureAwait(false);
            };

            _connection.Closed += async exception =>
            {
                // The automatic reconnect gave up (or the server closed it):
                // start again, forever, until told to stop.
                _logger.LogWarning(exception, "{Hub}: connection closed; starting it again.", Path);
                await ConnectLoopAsync(_running.Token).ConfigureAwait(false);
                await RaiseReconnectedAsync().ConfigureAwait(false);
            };
        }
        finally
        {
            _gate.Release();
        }

        await ConnectLoopAsync(_running!.Token).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            _running?.Cancel();

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _running?.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The server-to-client handlers.</summary>
    protected abstract void Register(HubConnection connection);

    private HubConnection Build()
    {
        var url = new Uri(new Uri(_settings.ApiBaseUrl.TrimEnd('/') + "/"), Path);

        return new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => _api.GetAccessTokenAsync();

                if (HandlerFactory is { } factory)
                {
                    options.HttpMessageHandlerFactory = _ => factory();
                    options.Transports = HttpTransportType.LongPolling;
                }
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();
    }

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; !cancellationToken.IsCancellationRequested; attempt++)
        {
            var connection = _connection;

            if (connection is null || connection.State != HubConnectionState.Disconnected)
            {
                return;
            }

            try
            {
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Hub}: connected.", Path);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (DeviceRevokedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "{Hub}: could not connect (attempt {Attempt}); trying again.", Path, attempt + 1);
            }

            try
            {
                await Task.Delay(Backoff[Math.Min(attempt, Backoff.Length - 1)] + TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RaiseReconnectedAsync()
    {
        if (Reconnected is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            try
            {
                await handler().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "{Hub}: a reconnect handler failed.", Path);
            }
        }
    }

    /// <summary>Retries for ever, backing off to every 30 seconds.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => Backoff[Math.Min((int)retryContext.PreviousRetryCount, Backoff.Length - 1)];
    }
}

/// <summary>
/// <c>/hubs/kiosk</c> for an activated kiosk (plan section 6): the queue, the
/// printer and configuration changes, and remote commands - the moment they
/// happen rather than at the next heartbeat (docs/TASKS.md T-062).
/// </summary>
public sealed class KioskHubClient : HubClientBase
{
    public KioskHubClient(KioskApiClient api, IOptions<KioskSettings> settings, ILogger<KioskHubClient> logger)
        : base(api, settings, logger)
    {
    }

    protected override string Path => "hubs/kiosk";

    public event Func<IReadOnlyList<QueueItem>, Task>? QueueUpdated;

    public event Func<Guid, Task>? JobAdded;

    public event Func<Guid, Task>? JobCancelled;

    public event Func<Task>? PrinterConfigChanged;

    public event Func<Task>? KioskConfigChanged;

    public event Func<Guid, string, Task>? CommandReceived;

    /// <summary>A payment this kiosk opened was paid (plan section 9.1, step 3): the payment and its job.</summary>
    public event Func<Guid, Guid, Task>? PaymentConfirmed;

    protected override void Register(HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.On<QueueMessage>("QueueUpdated", message => Raise(QueueUpdated, message.Queue ?? []));
        connection.On<JobMessage>("JobAdded", message => Raise(JobAdded, message.JobId));
        connection.On<JobMessage>("JobCancelled", message => Raise(JobCancelled, message.JobId));
        connection.On<PaymentMessage>("PaymentConfirmed", message => PaymentConfirmed is { } paid
            ? paid(message.PaymentId, message.JobId)
            : Task.CompletedTask);
        connection.On<KioskMessage>("PrinterConfigChanged", _ => Raise(PrinterConfigChanged));
        connection.On<KioskMessage>("KioskConfigChanged", _ => Raise(KioskConfigChanged));
        connection.On<CommandMessage>("KioskCommand", message => CommandReceived is { } handler
            ? handler(message.CommandId, message.Type)
            : Task.CompletedTask);
    }

    private static Task Raise<T>(Func<T, Task>? handler, T value) => handler is null ? Task.CompletedTask : handler(value);

    private static Task Raise(Func<Task>? handler) => handler is null ? Task.CompletedTask : handler();

    private sealed record QueueMessage(Guid KioskId, IReadOnlyList<QueueItem>? Queue);

    private sealed record JobMessage(Guid KioskId, Guid JobId);

    private sealed record KioskMessage(Guid KioskId);

    private sealed record PaymentMessage(Guid KioskId, Guid PaymentId, Guid JobId);

    private sealed record CommandMessage(Guid CommandId, string Type, string? Payload);
}

/// <summary>
/// <c>/hubs/provisioning</c> for a device on the activation screen (plan
/// section 7.11): activation, revocation and a renewed code arrive at once
/// instead of at the next poll (docs/TASKS.md T-062).
/// </summary>
public sealed class ProvisioningHubClient : HubClientBase
{
    public ProvisioningHubClient(KioskApiClient api, IOptions<KioskSettings> settings, ILogger<ProvisioningHubClient> logger)
        : base(api, settings, logger)
    {
    }

    protected override string Path => "hubs/provisioning";

    public event Func<Task>? DeviceActivated;

    public event Func<Task>? DeviceRevoked;

    public event Func<string, DateTime, Task>? PairingCodeRenewed;

    protected override void Register(HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.On<DeviceMessage>("DeviceActivated", _ => DeviceActivated is { } handler ? handler() : Task.CompletedTask);
        connection.On<DeviceMessage>("DeviceRevoked", _ => DeviceRevoked is { } handler ? handler() : Task.CompletedTask);
        connection.On<CodeMessage>("PairingCodeRenewed", message => PairingCodeRenewed is { } handler
            ? handler(message.PairingCode, message.ExpiresAt)
            : Task.CompletedTask);
    }

    private sealed record DeviceMessage(Guid DeviceId);

    private sealed record CodeMessage(Guid DeviceId, string PairingCode, DateTime ExpiresAt);
}
