using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;

using NeoPrint.Application.Operations;
using NeoPrint.Domain.Operations;

namespace NeoPrint.Infrastructure.Messaging;

/// <summary>
/// Reads MassTransit's dead-letter queues (<c>*_error</c>, <c>*_skipped</c>)
/// through RabbitMQ's management API - plain HTTP, no client package (T-215).
/// </summary>
/// <remarks>
/// <para>
/// The address is <c>RabbitMq:ManagementUrl</c>, or the broker's host on port
/// 15672; the account is the one in <c>NEOPRINT_RABBITMQ_URI</c> (from
/// OpenBao), which the broker's default user makes an administrator.
/// </para>
/// <para>
/// The age of a queue's oldest message comes from MassTransit's
/// <c>MT-Fault-Timestamp</c> header on the message at the head of the queue,
/// read with "requeue": the message goes straight back, and only a byte of
/// its body is asked for - the content (an e-mail address, an amount) is
/// never read, stored or logged.
/// </para>
/// </remarks>
public sealed class RabbitMqDeadLetterInspector : IDeadLetterInspector
{
    public const string HttpClientName = "rabbitmq-management";

    private static readonly string[] Suffixes = ["_error", "_skipped"];

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _configuration;

    public RabbitMqDeadLetterInspector(IHttpClientFactory http, IConfiguration configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<DeadLetterQueue>> InspectAsync(CancellationToken cancellationToken = default)
    {
        var (client, vhost) = Client();

        try
        {
            using (client)
            {
                var queues = await client
                    .GetFromJsonAsync<List<QueueInfo>>($"api/queues/{vhost}?columns=name,messages", cancellationToken)
                    .ConfigureAwait(false) ?? [];
                var result = new List<DeadLetterQueue>();

                foreach (var queue in queues.Where(queue => Suffixes.Any(suffix => queue.Name.EndsWith(suffix, StringComparison.Ordinal))))
                {
                    var oldest = queue.Messages > 0 ? await OldestAsync(client, vhost, queue.Name, cancellationToken).ConfigureAwait(false) : null;
                    result.Add(new DeadLetterQueue(queue.Name, queue.Messages, oldest));
                }

                return result;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new DeadLetterInspectionException($"rabbitmq management: {exception.GetType().Name}", exception);
        }
    }

    private static async Task<DateTime?> OldestAsync(HttpClient client, string vhost, string queue, CancellationToken cancellationToken)
    {
        using var response = await client
            .PostAsJsonAsync(
                $"api/queues/{vhost}/{Uri.EscapeDataString(queue)}/get",
                new { count = 1, ackmode = "ack_requeue_true", encoding = "auto", truncate = 1 },
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0
            || !document.RootElement[0].TryGetProperty("properties", out var properties))
        {
            return null;
        }

        if (properties.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
            && headers.TryGetProperty("MT-Fault-Timestamp", out var fault) && fault.ValueKind == JsonValueKind.String
            && DateTime.TryParse(fault.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var failed))
        {
            return failed;
        }

        return properties.TryGetProperty("timestamp", out var stamp) && stamp.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }

    private (HttpClient Client, string VHost) Client()
    {
        var broker = new Uri(_configuration["NEOPRINT_RABBITMQ_URI"] ?? throw new DeadLetterInspectionException("NEOPRINT_RABBITMQ_URI is not set"));
        var management = _configuration["RabbitMq:ManagementUrl"] is { Length: > 0 } configured
            ? new Uri(configured.EndsWith('/') ? configured : configured + "/")
            : new Uri($"http://{broker.Host}:15672/");
        var vhost = broker.AbsolutePath.Trim('/') is { Length: > 0 } path ? Uri.EscapeDataString(Uri.UnescapeDataString(path)) : "%2F";
        var user = broker.UserInfo.Split(':', 2);

        var client = _http.CreateClient(HttpClientName);
        client.BaseAddress = management;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.UnescapeDataString(user[0])}:{(user.Length > 1 ? Uri.UnescapeDataString(user[1]) : string.Empty)}")));

        return (client, vhost);
    }

    private sealed record QueueInfo(string Name, long Messages);
}
