using System.Collections.Concurrent;

using NeoPrint.Application.Notifications;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// An <see cref="IPushSender"/> that keeps what it was asked to send, in
/// place of FCM (there is no FCM in the test environment; development uses
/// <c>LoggingPushSender</c> - plan section 2.1).
/// </summary>
/// <remarks>
/// A token's prefix chooses FCM's answer: <c>gone-</c> is a token FCM no longer
/// knows, <c>flaky-</c> fails the first time and is delivered after.
/// </remarks>
public sealed class CapturedPushes : IPushSender
{
    private readonly ConcurrentQueue<SentPush> _sent = new();
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    /// <summary>Delivered pushes only.</summary>
    public IReadOnlyCollection<SentPush> Sent => [.. _sent];

    public int Attempts(string token) => _attempts.GetValueOrDefault(token);

    public IReadOnlyList<SentPush> To(string token) => [.. _sent.Where(push => push.Token == token)];

    public Task<PushResult> SendAsync(string token, PushMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var attempt = _attempts.AddOrUpdate(token, 1, (_, count) => count + 1);

        if (token.StartsWith("gone-", StringComparison.Ordinal))
        {
            return Task.FromResult(PushResult.TokenGone);
        }

        if (token.StartsWith("flaky-", StringComparison.Ordinal) && attempt == 1)
        {
            return Task.FromResult(PushResult.Failed);
        }

        _sent.Enqueue(new SentPush(token, message.Title, message.Body, message.Data));
        return Task.FromResult(PushResult.Delivered);
    }
}

public sealed record SentPush(string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data);
