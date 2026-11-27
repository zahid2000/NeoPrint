using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

using NeoPrint.Api.Infrastructure;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>Traces (T-479): what is sampled and what is left out. No Docker.</summary>
public sealed class MonitoringRegistrationTests
{
    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("0.25", 0.25)]
    [InlineData("7", 1.0)]
    [InlineData("-1", 0.0)]
    [InlineData("half", 1.0)]
    public void The_sample_ratio_is_read_and_kept_between_zero_and_one(string? configured, double expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Monitoring:TraceSampleRatio"] = configured })
            .Build();

        MonitoringRegistration.SampleRatio(configuration).Should().Be(expected);
    }

    [Theory]
    [InlineData("/health/live", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/hubs/kiosk", true)]
    [InlineData("/api/files", false)]
    [InlineData("/refund/abc", false)]
    public void Probes_and_live_connections_are_not_traced(string path, bool noise) =>
        MonitoringRegistration.IsNoise(new PathString(path)).Should().Be(noise);

    [Fact]
    public void A_trace_follows_outgoing_http_mongodb_and_rabbitmq() =>
        MonitoringRegistration.TraceSources.Should().BeEquivalentTo("System.Net.Http", "MongoDB.Driver", "MassTransit");
}
