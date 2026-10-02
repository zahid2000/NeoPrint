using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Tests.Behaviors;

/// <summary>
/// The level a request's outcome is logged at (<c>docs/OBSERVABILITY.md</c>):
/// a refused rule is a warning, only an unexpected failure is an error.
/// </summary>
public sealed class LoggingBehaviorTests
{
    private readonly CapturingLogger _logger = new();

    [Fact]
    public async Task A_refused_rule_is_a_warning_with_its_key_and_no_stack_trace()
    {
        var act = () => RunAsync(new DomainException("devices.revokedReinstall"));

        await act.Should().ThrowAsync<DomainException>();
        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("devices.revokedReinstall").And.Contain("Rejected");
        entry.Exception.Should().BeNull();
    }

    [Fact]
    public async Task Something_that_is_not_there_and_a_refused_sign_in_are_warnings_too()
    {
        await FluentActions.Awaiting(() => RunAsync(new NotFoundException("fiscalReceipt"))).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => RunAsync(new AuthenticationException("auth.invalidCredentials"))).Should().ThrowAsync<AuthenticationException>();

        _logger.Entries.Should().HaveCount(2).And.OnlyContain(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
    }

    [Fact]
    public async Task An_unexpected_failure_stays_an_error_with_the_exception()
    {
        var failure = new InvalidOperationException("the database went away");

        await FluentActions.Awaiting(() => RunAsync(failure)).Should().ThrowAsync<InvalidOperationException>();

        var entry = _logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(failure);
    }

    private Task<string> RunAsync(Exception failure)
    {
        var behavior = new LoggingBehavior<Probe, string>(_logger, new Correlation(), new NoUser());
        return behavior.Handle(new Probe(), _ => Task.FromException<string>(failure), CancellationToken.None);
    }

    private sealed record Probe;

    private sealed class Correlation : ICorrelationContext
    {
        public string CorrelationId => "test";

        public void Set(string correlationId)
        {
        }
    }

    private sealed class NoUser : ICurrentUser
    {
        public Guid? UserId => null;

        public Guid? AdminUserId => null;

        public Guid? KioskId => null;

        public Guid? DeviceId => null;

        public string? Ip => null;

        public string? AuditReason => null;
    }

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<LoggingBehavior<Probe, string>>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }
}
