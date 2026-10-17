using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Operations;

namespace NeoPrint.Application.Operations;

/// <summary>The runs of the background jobs (<c>job_runs</c>, T-215).</summary>
public interface IJobRunStore
{
    Task AddAsync(JobRun run, CancellationToken cancellationToken = default);

    /// <summary>The last run of each of <paramref name="jobs"/>; a job that never ran is missing.</summary>
    Task<IReadOnlyDictionary<string, JobRun>> LatestAsync(IReadOnlyCollection<string> jobs, CancellationToken cancellationToken = default);
}

/// <summary>Reads the message broker's dead-letter queues (MassTransit's <c>_error</c> and <c>_skipped</c>).</summary>
public interface IDeadLetterInspector
{
    /// <summary>Every dead-letter queue, empty ones too.</summary>
    /// <exception cref="DeadLetterInspectionException">The broker could not be asked.</exception>
    Task<IReadOnlyList<DeadLetterQueue>> InspectAsync(CancellationToken cancellationToken = default);
}

public sealed class DeadLetterInspectionException : Exception
{
    public DeadLetterInspectionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// DLQ monitoring (plan section 6, T-215): a message that failed every retry
/// sits in a dead-letter queue until somebody looks. A queue with messages
/// is a <c>High</c> incident - one per queue - closed by the system once the
/// queue is empty again; the counts and the oldest message's age go to the
/// admin panel.
/// </summary>
public sealed record RunDeadLetterCheckCommand : IRequest<JobRun>;

public sealed class RunDeadLetterCheckHandler : IRequestHandler<RunDeadLetterCheckCommand, JobRun>
{
    private readonly IDeadLetterInspector _inspector;
    private readonly IncidentReporter _incidents;
    private readonly IJobRunStore _runs;
    private readonly IClock _clock;
    private readonly ILogger<RunDeadLetterCheckHandler> _logger;

    public RunDeadLetterCheckHandler(
        IDeadLetterInspector inspector, IncidentReporter incidents, IJobRunStore runs, IClock clock, ILogger<RunDeadLetterCheckHandler> logger)
    {
        _inspector = inspector;
        _incidents = incidents;
        _runs = runs;
        _clock = clock;
        _logger = logger;
    }

    public static string KeyOf(string queue) => $"dead-letter:{queue}";

    public async Task<JobRun> Handle(RunDeadLetterCheckCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var run = new JobRun { Job = JobNames.DeadLetters, StartedAt = _clock.UtcNow };

        try
        {
            var queues = await _inspector.InspectAsync(cancellationToken).ConfigureAwait(false);
            run.Queues = [.. queues.OrderByDescending(queue => queue.Messages).ThenBy(queue => queue.Name, StringComparer.Ordinal)];
            run.Counts["queues"] = queues.Count;
            run.Counts["messages"] = queues.Sum(queue => queue.Messages);

            foreach (var queue in queues)
            {
                NeoPrintMetrics.DeadLetterMessages.Record(queue.Messages, new KeyValuePair<string, object?>("queue", queue.Name));

                if (queue.Messages > 0)
                {
                    var hours = queue.OldestAt is { } oldest ? (int)Math.Max(0, (_clock.UtcNow - oldest).TotalHours) : (int?)null;

                    await _incidents
                        .RaiseAsync(
                            IncidentType.Other,
                            IncidentSeverity.High,
                            hours is null
                                ? UserMessage.Of("incidents.deadLetters", ("name", queue.Name), ("count", queue.Messages))
                                : UserMessage.Of("incidents.deadLettersAged", ("name", queue.Name), ("count", queue.Messages), ("hours", hours)),
                            KeyOf(queue.Name),
                            cancellationToken,
                            entityType: "Queue")
                        .ConfigureAwait(false);
                }
                else
                {
                    await _incidents
                        .ResolveAsync(KeyOf(queue.Name), new UserMessage("incidents.deadLettersCleared"), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (DeadLetterInspectionException exception)
        {
            // Not an incident of its own: the broker being out of reach stops
            // the whole bus, which the health checks report. The panel says
            // the queues could not be read.
            run.Succeeded = false;
            run.Error = exception.Message;
            _logger.LogWarning(exception, "The dead-letter queues could not be read.");
        }

        run.FinishedAt = _clock.UtcNow;
        await _runs.AddAsync(run, cancellationToken).ConfigureAwait(false);

        return run;
    }
}
