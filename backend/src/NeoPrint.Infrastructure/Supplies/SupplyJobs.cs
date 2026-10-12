using MassTransit;

using MediatR;

using NeoPrint.Application.Supplies;
using NeoPrint.Contracts;

namespace NeoPrint.Infrastructure.Supplies;

/// <summary>
/// <c>SupplyEvaluator</c> on its event (plan section 6): a kiosk's paper
/// counter moved, or its printer said a cartridge was changed.
/// </summary>
public sealed class SupplyEvaluatorConsumer : IConsumer<SupplyLevelChanged>
{
    private readonly ISender _sender;

    public SupplyEvaluatorConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<SupplyLevelChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new EvaluateKioskSuppliesCommand(context.Message.KioskId), context.CancellationToken);
    }
}

/// <summary>
/// The supply module's recurring work (plan section 6): the evaluator's
/// five-minute sweep, the hourly forecast, the escalation of Critical tasks
/// nobody started, and the warehouse's low stock.
/// </summary>
public sealed class SupplyJobs
{
    public const string EvaluatorJobId = "supply-evaluator";

    public const string ForecastJobId = "supply-forecast";

    public const string EscalationJobId = "service-task-escalation";

    public const string LowStockJobId = "inventory-low-stock";

    public const string EveryFiveMinutes = "*/5 * * * *";

    public const string Hourly = "7 * * * *";

    /// <summary>Not on the hour: the forecast has it.</summary>
    public const string HourlyAtHalfPast = "37 * * * *";

    private readonly ISender _sender;

    public SupplyJobs(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
    public Task EvaluateAsync(CancellationToken cancellationToken) => _sender.Send(new EvaluateAllSuppliesCommand(), cancellationToken);

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
    public Task ForecastAsync(CancellationToken cancellationToken) => _sender.Send(new RunSupplyForecastCommand(), cancellationToken);

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
    public Task EscalateAsync(CancellationToken cancellationToken) => _sender.Send(new EscalateOverdueServiceTasksCommand(), cancellationToken);

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
    public Task LowStockAsync(CancellationToken cancellationToken) => _sender.Send(new CheckInventoryLowStockCommand(), cancellationToken);
}
