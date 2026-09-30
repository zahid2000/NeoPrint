using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Hangfire's client in an HTTP-only test host. The real one configures
/// Hangfire's process-wide settings - its logger among them - the first time
/// it is used; a second host doing that and then being disposed leaves the
/// fixture's Hangfire logging through a disposed logger factory, and every
/// later enqueue fails. This one touches nothing global.
/// </summary>
internal sealed class NoBackgroundJobs : IBackgroundJobClient, IRecurringJobManager
{
    public string Create(Job job, IState state)
        => throw new InvalidOperationException("An HTTP-only test host runs no background jobs; use the fixture's host.");

    public bool ChangeState(string jobId, IState state, string expectedState) => false;

    public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
    {
        // Recurring jobs are the fixture's host's.
    }

    public void Trigger(string recurringJobId)
    {
    }

    public void RemoveIfExists(string recurringJobId)
    {
    }
}
