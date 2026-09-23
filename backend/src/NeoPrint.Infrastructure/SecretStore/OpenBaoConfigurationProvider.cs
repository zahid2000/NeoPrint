using Microsoft.Extensions.Configuration;

namespace NeoPrint.Infrastructure.SecretStore;

/// <summary>
/// Loads the OpenBao secrets into <see cref="IConfiguration"/> under their own
/// key names, so the rest of the code never knows where a value came from
/// (plan section 2.13).
/// </summary>
public sealed class OpenBaoConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly ISecretStore _store;
    private readonly OpenBaoOptions _options;
    private readonly Action<string, Exception?> _log;
    private readonly TimeProvider _time;
    private readonly bool _validateRequiredSecrets;

    private readonly CancellationTokenSource _stopping = new();
    private Task? _refreshLoop;

    public OpenBaoConfigurationProvider(
        ISecretStore store,
        OpenBaoOptions options,
        bool validateRequiredSecrets,
        Action<string, Exception?>? log = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _options = options;
        _validateRequiredSecrets = validateRequiredSecrets;
        _log = log ?? ((_, _) => { });
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True once at least one load has succeeded.</summary>
    public bool HasValues { get; private set; }

    /// <inheritdoc />
    public override void Load() => LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Performs the initial load, retrying with exponential backoff for
    /// <see cref="OpenBaoOptions.StartupTimeout"/>.
    /// </summary>
    /// <exception cref="SecretStoreUnavailableException">
    /// OpenBao stayed unreachable and <see cref="OpenBaoOptions.Optional"/> is false.
    /// </exception>
    /// <exception cref="MissingSecretsException">A mandatory secret is absent.</exception>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _options.StartupTimeout;
        var delay = _options.InitialRetryDelay;
        SecretStoreException? lastFailure = null;

        while (true)
        {
            try
            {
                var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);

                if (_validateRequiredSecrets)
                {
                    var missing = RequiredSecrets.FindMissing(values);
                    if (missing.Count > 0)
                    {
                        // A configuration error, not an outage: retrying will
                        // not help, so fail immediately and name the keys.
                        throw new MissingSecretsException(missing);
                    }
                }

                Data = values;
                HasValues = true;
                _log($"Loaded {values.Count} secrets from OpenBao.", null);
                return;
            }
            catch (SecretStoreUnavailableException exception)
            {
                lastFailure = exception;

                var remaining = deadline - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                _log($"OpenBao is not available yet ({exception.Message}); retrying in {delay.TotalSeconds:0}s.", null);

                await Task.Delay(delay < remaining ? delay : remaining, _time, cancellationToken).ConfigureAwait(false);
                delay = delay < _options.MaxRetryDelay ? delay * 2 : _options.MaxRetryDelay;
            }
        }

        if (_options.Optional)
        {
            // Outside production a developer can still start the application
            // without the secret store; it will fail later, where it is used.
            _log($"OpenBao is unavailable and has been configured as optional; continuing without secrets. " +
                 $"Last error: {lastFailure?.Message}", null);
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        throw new SecretStoreUnavailableException(
            $"OpenBao at {_options.Address} did not become available within {_options.StartupTimeout.TotalSeconds:0} seconds. " +
            "The application cannot start without its secrets (docs/OPENBAO.md).",
            lastFailure);
    }

    /// <summary>
    /// Starts the background refresh so that rotated secrets take effect
    /// without a restart.
    /// </summary>
    public void StartRefreshing()
    {
        _refreshLoop ??= Task.Run(() => RefreshLoopAsync(_stopping.Token));
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.RefreshInterval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);

                if (_validateRequiredSecrets && RequiredSecrets.FindMissing(values).Count > 0)
                {
                    // Keep serving the values we already have rather than
                    // taking the service down over a transient bad read.
                    _log("A refresh from OpenBao came back without all mandatory secrets; keeping the previous values.", null);
                    continue;
                }

                if (HasSameValues(values))
                {
                    continue;
                }

                Data = values;
                HasValues = true;

                // Wakes up IOptionsMonitor, so rotated SMTP, Telegram and
                // provider credentials apply without a restart.
                OnReload();
                _log("OpenBao secrets changed; configuration reloaded.", null);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // Plan section 2.13: if OpenBao drops out while running, carry
                // on with the values last loaded.
                _log("Refreshing the secrets from OpenBao failed; continuing with the last known values.", exception);
            }
        }
    }

    private async Task<Dictionary<string, string?>> ReadAllAsync(CancellationToken cancellationToken)
    {
        // Case-insensitive, to match how IConfiguration looks keys up.
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _options.Paths)
        {
            var secrets = await _store.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (var (key, value) in secrets)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private bool HasSameValues(Dictionary<string, string?> candidate)
    {
        if (candidate.Count != Data.Count)
        {
            return false;
        }

        foreach (var (key, value) in candidate)
        {
            if (!Data.TryGetValue(key, out var existing) || !string.Equals(existing, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
