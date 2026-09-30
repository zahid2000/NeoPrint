using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Infrastructure.Payments;

/// <inheritdoc cref="IPaymentProviders" />
public sealed class PaymentProviders : IPaymentProviders
{
    private readonly IReadOnlyList<IBankQrProvider> _banks;

    public PaymentProviders(IEnumerable<IBankQrProvider> banks, ICardPaymentProvider card)
    {
        _banks = banks.ToList();
        BankQr = _banks.FirstOrDefault() ?? throw new InvalidOperationException("No bank QR provider is registered.");
        Card = card;
    }

    public IBankQrProvider BankQr { get; }

    public ICardPaymentProvider Card { get; }

    public IBankQrProvider? Find(string name)
        => _banks.FirstOrDefault(bank => string.Equals(bank.Name, name, StringComparison.OrdinalIgnoreCase));

    public IRefundingProvider? FindRefunding(string name)
        => string.Equals(Card.Name, name, StringComparison.OrdinalIgnoreCase) ? Card : Find(name);
}

/// <summary>
/// <c>PaymentExpirationJob</c> (every minute) and <c>PaymentReconciliationJob</c>
/// (every five) from plan section 6, run by Hangfire.
/// </summary>
/// <remarks>
/// Each payment is its own command and its own transaction: one that fails -
/// the bank does not answer, somebody else saved it first - is logged and
/// picked up again on the next run, and does not hold up the rest.
/// </remarks>
public sealed class PaymentJobs
{
    public const string ExpirationJobId = "payment-expiration";

    public const string ReconciliationJobId = "payment-reconciliation";

    private const int Batch = 100;

    /// <summary>How far back the reconciliation looks: a bank answer older than this is an incident, not a job.</summary>
    private static readonly TimeSpan ReconcileWindow = TimeSpan.FromDays(7);

    private readonly ISender _sender;
    private readonly IPaymentRepository _payments;
    private readonly IPrintJobRepository _jobs;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<PaymentJobs> _logger;

    public PaymentJobs(
        ISender sender,
        IPaymentRepository payments,
        IPrintJobRepository jobs,
        ISettingsReader settings,
        IClock clock,
        ILogger<PaymentJobs> logger)
    {
        _sender = sender;
        _payments = payments;
        _jobs = jobs;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Overdue intents expire (after one last question to the bank); jobs nobody started paying for expire too.</summary>
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task ExpireAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        foreach (var payment in await _payments.ListOverdueAsync(now, Batch, cancellationToken).ConfigureAwait(false))
        {
            await RunAsync(new ExpirePaymentCommand(payment.Id), payment.Id, cancellationToken).ConfigureAwait(false);
        }

        var minutes = await _settings.GetIntAsync(SettingKeys.PaymentTimeoutMinutes, 5, cancellationToken).ConfigureAwait(false);
        var cutoff = now.AddMinutes(-Math.Max(1, minutes));

        foreach (var job in await _jobs.ListAwaitingPaymentAsync(cutoff, Batch, cancellationToken).ConfigureAwait(false))
        {
            await RunAsync(new ExpireUnpaidJobCommand(job.Id), job.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Payments the bank may know better about: its status decides (late or lost callbacks).</summary>
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var after = await _settings.GetIntAsync(SettingKeys.PaymentReconcileAfterMinutes, 10, cancellationToken).ConfigureAwait(false);

        var due = await _payments
            .ListToReconcileAsync(now.AddMinutes(-Math.Max(1, after)), now - ReconcileWindow, Batch, cancellationToken)
            .ConfigureAwait(false);

        foreach (var payment in due)
        {
            // A card charge is settled from the acquirer's status; a bank QR is reconciled with the bank.
            if (payment.Provider == MockCardProvider.ProviderName)
            {
                await RunAsync(new SettleCardPaymentCommand(payment.Id), payment.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunAsync(new ReconcilePaymentCommand(payment.Id), payment.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync<TCommand>(TCommand command, Guid id, CancellationToken cancellationToken)
        where TCommand : notnull
    {
        try
        {
            await _sender.Send(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ConcurrencyException or PaymentProviderUnavailableException or MongoDB.Driver.MongoException)
        {
            _logger.LogWarning(exception, "{Command} for {Id} did not go through; the next run tries again.", typeof(TCommand).Name, id);
        }
    }
}
