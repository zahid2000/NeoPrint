using Microsoft.Extensions.Configuration;

namespace NeoPrint.Infrastructure.SecretStore;

/// <summary>
/// Registers OpenBao as a configuration source.
/// </summary>
public static class OpenBaoConfigurationExtensions
{
    /// <summary>
    /// Adds the OpenBao secrets to the configuration.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="configure">Adjusts the bootstrap settings read from the environment.</param>
    /// <param name="isProduction">
    /// In production a missing mandatory secret, or an unreachable store, stops
    /// the application from starting (plan section 2.13).
    /// </param>
    /// <param name="log">Where progress and failures are reported during startup.</param>
    public static IConfigurationBuilder AddOpenBao(
        this IConfigurationBuilder builder,
        Action<OpenBaoOptions>? configure = null,
        bool isProduction = true,
        Action<string, Exception?>? log = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = OpenBaoOptions.FromEnvironment();
        configure?.Invoke(options);

        return builder.Add(new OpenBaoConfigurationSource(options, isProduction, log));
    }
}

/// <summary>
/// Configuration source backing <see cref="OpenBaoConfigurationProvider"/>.
/// </summary>
public sealed class OpenBaoConfigurationSource : IConfigurationSource
{
    private readonly OpenBaoOptions _options;
    private readonly bool _isProduction;
    private readonly Action<string, Exception?>? _log;

    public OpenBaoConfigurationSource(OpenBaoOptions options, bool isProduction, Action<string, Exception?>? log)
    {
        _options = options;
        _isProduction = isProduction;
        _log = log;
    }

    /// <summary>The provider built for this source, exposed for health checks.</summary>
    public OpenBaoConfigurationProvider? Provider { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        _options.Validate();

        var http = new HttpClient
        {
            BaseAddress = new Uri(_options.Address.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };

        var client = new OpenBaoClient(http, _options, _log);

        var provider = new OpenBaoConfigurationProvider(
            client,
            _options,
            // Outside production a developer may be running only part of the
            // stack; missing optional secrets should not block startup there.
            validateRequiredSecrets: _isProduction,
            _log);

        provider.StartRefreshing();
        Provider = provider;

        return provider;
    }
}
