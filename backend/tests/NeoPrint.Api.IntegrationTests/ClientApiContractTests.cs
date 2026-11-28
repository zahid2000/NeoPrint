using System.Text.Json.Nodes;

using NeoPrint.Crypto.Releases;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The backend keeps serving what the kiosks, provisioning scripts and mobile
/// apps already in the field call (ADR-0009, T-491).
/// </summary>
/// <remarks>
/// <para>
/// <c>KioskClientContractTests</c> runs today's kiosk code against today's
/// API. A kiosk a rollout has not reached yet runs yesterday's: it calls
/// what <c>contracts/client-api.json</c> lists, and expects the shapes in
/// <c>contracts/client-api.shapes.json</c>. An endpoint, a response field or
/// a hub event leaves only when <c>minSupported</c> has passed the client
/// version that stopped using it (<c>retiredIn</c>).
/// </para>
/// <para>
/// Needs no Docker: the backend is read by reflection, the clients as text.
/// With <c>NEOPRINT_CONTRACT_UPDATE=1</c> it adds what a client newly calls
/// and rewrites the shapes - a change that breaks an old client fails all the same.
/// </para>
/// </remarks>
public sealed class ClientApiContractTests
{
    private const string UpdateHint =
        "Run: NEOPRINT_CONTRACT_UPDATE=1 dotnet test backend/tests/NeoPrint.Api.IntegrationTests --filter ClientApiContractTests (docs/DEV_ENVIRONMENT.md).";

    private static readonly Lazy<JsonObject> Contract = new(Prepare);

    private static JsonArray Endpoints => (JsonArray)Contract.Value["endpoints"]!;

    private static JsonArray Events => (JsonArray)Contract.Value["events"]!;

    /// <summary>The contract, with what the clients newly call added when updating.</summary>
    private static JsonObject Prepare()
    {
        var contract = ClientApiContract.LoadContract();
        if (!ClientApiContract.Updating)
        {
            return contract;
        }

        var endpoints = (JsonArray)contract["endpoints"]!;
        foreach (var use in ClientApiContract.ClientUses)
        {
            foreach (var endpoint in ClientApiContract.Served(use).Where(endpoint => !Rows(endpoints, use.Client, [endpoint]).Any()))
            {
                endpoints.Add(new JsonObject { ["client"] = use.Client, ["method"] = endpoint.Method, ["route"] = endpoint.Route });
            }
        }

        var events = (JsonArray)contract["events"]!;
        foreach (var used in ClientApiContract.ClientEvents.DistinctBy(e => (e.Client, e.Name)))
        {
            if (!events.Any(e => e!["client"]!.GetValue<string>() == used.Client && e["name"]!.GetValue<string>() == used.Name))
            {
                events.Add(new JsonObject { ["client"] = used.Client, ["name"] = used.Name });
            }
        }

        contract["endpoints"] = new JsonArray([.. endpoints
            .OrderBy(e => e!["client"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(e => e!["route"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(e => e!["method"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(e => e!.DeepClone())]);
        contract["events"] = new JsonArray([.. events
            .OrderBy(e => e!["client"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(e => e!["name"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(e => e!.DeepClone())]);

        ClientApiContract.Save(ClientApiContract.ContractPath, contract);
        return contract;
    }

    /// <summary>The contract's rows for those endpoints, of that client.</summary>
    private static IEnumerable<JsonNode> Rows(JsonArray endpoints, string client, IReadOnlyCollection<BackendEndpoint> served)
        => endpoints.Where(entry => entry!["client"]!.GetValue<string>() == client
            && served.Any(endpoint => endpoint.Method == entry["method"]!.GetValue<string>() && endpoint.Route == entry["route"]!.GetValue<string>()))!;

    private static bool Active(JsonNode entry) => ClientApiContract.IsActive(Contract.Value, entry);

    [Fact]
    public void The_contract_is_well_formed()
    {
        var problems = new List<string>();

        foreach (var client in ClientApiContract.Clients)
        {
            var min = Contract.Value["minSupported"]?[client];
            if (client == "provisioning")
            {
                // A script on a flash drive has no version a kiosk reports: it is never retired by one.
                if (min is not null)
                {
                    problems.Add("minSupported.provisioning: a provisioning script is not versioned - leave it out.");
                }
            }
            else if (min is null || !ReleaseVersion.TryParse(min.GetValue<string>(), out _))
            {
                problems.Add($"minSupported.{client}: a release version is needed (major.minor.patch[-pre]).");
            }
        }

        foreach (var entry in Endpoints.Concat(Events))
        {
            var client = entry!["client"]?.GetValue<string>();
            if (client is null || !ClientApiContract.Clients.Contains(client))
            {
                problems.Add($"{entry.ToJsonString()}: an unknown client.");
                continue;
            }

            if (entry["retiredIn"]?.GetValue<string>() is { } retired)
            {
                if (client == "provisioning")
                {
                    problems.Add($"{entry.ToJsonString()}: a provisioning script is never retired by a version - a decision in docs/DECISIONS.md removes it.");
                }
                else if (!ReleaseVersion.TryParse(retired, out _))
                {
                    problems.Add($"{entry.ToJsonString()}: retiredIn is not a release version.");
                }
            }

            foreach (var (field, version) in entry["retiredFields"]?.AsObject() ?? [])
            {
                if (!ReleaseVersion.TryParse(version?.GetValue<string>(), out _))
                {
                    problems.Add($"{entry.ToJsonString()}: retiredFields.{field} is not a release version.");
                }
            }
        }

        foreach (var entry in Endpoints)
        {
            if (entry!["method"]?.GetValue<string>() is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH"))
            {
                problems.Add($"{entry.ToJsonString()}: an HTTP method is needed.");
            }
        }

        problems.AddRange(Endpoints
            .GroupBy(e => (e!["client"]?.GetValue<string>(), e["method"]?.GetValue<string>(), e["route"]?.GetValue<string>()))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: listed twice."));

        problems.Should().BeEmpty(problems.Lines());
    }

    [Fact]
    public void Every_path_a_client_calls_is_in_the_contract()
    {
        var problems = new List<string>();

        foreach (var use in ClientApiContract.ClientUses)
        {
            var served = ClientApiContract.Served(use);
            var listed = Rows(Endpoints, use.Client, served).ToList();
            if (served.Count == 0)
            {
                problems.Add($"{use}: the backend serves no such path.");
            }
            else if (listed.Count == 0)
            {
                problems.Add($"{use}: {served.First().Key} is not in contracts/client-api.json. {UpdateHint}");
            }
            else if (!listed.Any(Active))
            {
                problems.Add($"{use}: still called, but contracts/client-api.json says it is retired.");
            }
        }

        // A row no client calls any more: the version that stopped says when it may go.
        foreach (var entry in Endpoints.Where(entry => Active(entry!) && entry!["manual"] is null && entry["retiredIn"] is null))
        {
            var client = entry!["client"]!.GetValue<string>();
            var route = entry["route"]!.GetValue<string>();
            var method = entry["method"]!.GetValue<string>();
            if (!ClientApiContract.ClientUses.Any(use => use.Client == client
                    && ClientApiContract.Served(use).Any(endpoint => endpoint.Method == method && endpoint.Route == route)))
            {
                problems.Add($"{client}: {entry["method"]} {route} is no longer called - set \"retiredIn\" to the {client} version that stopped calling it (ADR-0009); do not delete the row.");
            }
        }

        problems.Distinct().Should().BeEmpty(problems.Distinct().Lines());
    }

    [Fact]
    public void Every_endpoint_a_supported_client_calls_is_served()
    {
        var served = ClientApiContract.BackendEndpoints.Select(endpoint => endpoint.Key).ToHashSet(StringComparer.Ordinal);

        var missing = Endpoints
            .Where(entry => Active(entry!))
            .Where(entry => !served.Contains(entry!["method"]!.GetValue<string>() + " " + entry["route"]!.GetValue<string>()))
            .Select(entry => $"{entry!["client"]}: {entry["method"]} {entry["route"]} is no longer served, but a supported {entry["client"]} calls it "
                + $"(minSupported {Contract.Value["minSupported"]?[entry["client"]!.GetValue<string>()]}). Keep it until minSupported passes the version that stopped calling it (ADR-0009).")
            .ToList();

        missing.Should().BeEmpty(missing.Lines());
    }

    [Fact]
    public void What_a_supported_client_sends_and_reads_keeps_its_shape()
    {
        var active = Endpoints.Where(entry => Active(entry!)).ToList();
        var keys = active.Select(entry => entry!["method"]!.GetValue<string>() + " " + entry["route"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

        var current = new JsonObject();
        foreach (var key in keys.Order(StringComparer.Ordinal))
        {
            var endpoint = ClientApiContract.BackendEndpoints.FirstOrDefault(endpoint => endpoint.Key == key);
            if (endpoint is not null)
            {
                current[key] = ClientApiContract.ShapeOf(endpoint);
            }
        }

        var before = File.Exists(ClientApiContract.ShapesPath)
            ? (JsonObject)JsonNode.Parse(File.ReadAllText(ClientApiContract.ShapesPath))!
            : [];

        var breaking = new List<string>();
        foreach (var (key, shape) in before)
        {
            // A row goes from the contract only once its endpoint is retired and the shapes no longer hold it.
            if (!Endpoints.Any(entry => entry!["method"]!.GetValue<string>() + " " + entry["route"]!.GetValue<string>() == key))
            {
                breaking.Add($"{key} - its row was deleted from contracts/client-api.json while a supported client may call it; set \"retiredIn\" instead");
                continue;
            }

            if (!keys.Contains(key) || current[key] is not { } now)
            {
                // Retired, or not served - which the test above reports.
                continue;
            }

            var allowed = RetiredFields(active, key);
            var found = new List<string>();
            ClientApiContract.Compare(shape?["request"], now["request"], string.Empty, request: true, allowed, found);
            ClientApiContract.Compare(shape?["response"], now["response"], string.Empty, request: false, allowed, found);
            breaking.AddRange(found.Select(problem => $"{key} - {(problem.StartsWith(':') ? "body" + problem : problem)}"));
        }

        breaking.Should().BeEmpty(
            "an old client would break (ADR-0009: add, do not change; a field goes when minSupported passes its retiredFields version):"
            + Environment.NewLine + breaking.Lines());

        var same = JsonNode.DeepEquals(before, current);
        if (!same && ClientApiContract.Updating)
        {
            ClientApiContract.Save(ClientApiContract.ShapesPath, current);
            return;
        }

        same.Should().BeTrue($"contracts/client-api.shapes.json is behind the backend - nothing breaks, the new shape has to be recorded. {UpdateHint}");
    }

    [Fact]
    public void Every_hub_event_a_supported_client_listens_for_is_still_sent()
    {
        var problems = new List<string>();

        foreach (var used in ClientApiContract.ClientEvents)
        {
            if (!Events.Any(e => e!["client"]!.GetValue<string>() == used.Client && e["name"]!.GetValue<string>() == used.Name))
            {
                problems.Add($"{used.Client}: the hub event {used.Name} ({used.File}:{used.Line}) is not in contracts/client-api.json. {UpdateHint}");
            }
        }

        foreach (var entry in Events.Where(entry => Active(entry!)))
        {
            var name = entry!["name"]!.GetValue<string>();
            var client = entry["client"]!.GetValue<string>();

            if (!ClientApiContract.BackendSource.Contains('"' + name + '"', StringComparison.Ordinal))
            {
                problems.Add($"{client}: the hub event {name} is no longer sent, but a supported {client} listens for it (ADR-0009).");
            }

            if (entry["retiredIn"] is null && !ClientApiContract.ClientEvents.Any(e => e.Client == client && e.Name == name))
            {
                problems.Add($"{client}: the hub event {name} is no longer listened for - set \"retiredIn\" to the {client} version that stopped (ADR-0009).");
            }
        }

        problems.Should().BeEmpty(problems.Lines());
    }

    [Theory]
    [InlineData("""HttpMethod.Get, "api/kiosks/me/config", cancellationToken)""", "kiosk", "api/kiosks/me/config")]
    [InlineData("""$"api/print-jobs/{jobId}/status?wait={wait}", ct)""", "kiosk", "api/print-jobs/{}/status")]
    [InlineData("""$"api/print-jobs/by-code/{Uri.EscapeDataString(code)}")""", "kiosk", "api/print-jobs/by-code/{}")]
    [InlineData("""path: 'api/files/$id/pages/$page.png',""", "mobile", "api/files/{}/pages/{}.png")]
    [InlineData("""path: 'api/files/$id/versions/${current['id']}',""", "mobile", "api/files/{}/versions/{}")]
    [InlineData("url=\"$base/api/releases/latest/$component/linux-x64?channel=$channel\"", "provisioning", "api/releases/latest/{}/linux-x64")]
    [InlineData("$url = \"$Base/api/releases/latest/$($component)/win-x64\"", "provisioning", "api/releases/latest/{}/win-x64")]
    public void A_path_is_read_as_the_backend_routes_it(string line, string client, string expected)
        => ClientApiContract.PathsIn(line, client).Should().Equal(expected);

    [Fact]
    public void A_changed_response_breaks_an_old_client_and_an_added_field_does_not()
    {
        var before = JsonNode.Parse("""{ "id": "guid", "status": "enum(Done|Open)", "name": "string", "items": { "list": { "n": "integer" } } }""");
        var added = JsonNode.Parse("""{ "id": "guid", "status": "enum(Done|Late|Open)", "name": "string", "extra": "string?", "items": { "list": { "n": "integer" } } }""");
        var broken = JsonNode.Parse("""{ "id": "string", "status": "enum(Open)", "name": "string?", "items": { "list": { } } }""");

        var none = new List<string>();
        ClientApiContract.Compare(before, added, string.Empty, request: false, new HashSet<string>(), none);
        none.Should().BeEmpty();

        var found = new List<string>();
        ClientApiContract.Compare(before, broken, string.Empty, request: false, new HashSet<string>(), found);
        found.Should().HaveCount(4).And.Contain(p => p.StartsWith("items.list.n: removed", StringComparison.Ordinal));

        var retired = new List<string>();
        ClientApiContract.Compare(before, JsonNode.Parse("""{ "id": "guid", "status": "enum(Done|Open)", "items": { "list": { "n": "integer" } } }"""),
            string.Empty, request: false, new HashSet<string> { "name" }, retired);
        retired.Should().BeEmpty("a field past its retiredFields version may go");
    }

    [Fact]
    public void A_new_required_request_field_breaks_an_old_client_and_an_optional_one_does_not()
    {
        var before = JsonNode.Parse("""{ "name!": "string" }""");

        var optional = new List<string>();
        ClientApiContract.Compare(before, JsonNode.Parse("""{ "name!": "string", "note": "string?" }"""), string.Empty, request: true, new HashSet<string>(), optional);
        optional.Should().BeEmpty();

        var required = new List<string>();
        ClientApiContract.Compare(before, JsonNode.Parse("""{ "name!": "string", "note!": "string" }"""), string.Empty, request: true, new HashSet<string>(), required);
        required.Should().ContainSingle().Which.Should().StartWith("note");
    }

    /// <summary>The response fields of an endpoint whose last reader is no longer supported.</summary>
    private static HashSet<string> RetiredFields(IEnumerable<JsonNode?> active, string key)
    {
        // A field may go only when every supported client that reads the endpoint has let it go.
        HashSet<string>? allowed = null;
        foreach (var entry in active.Where(e => e!["method"]!.GetValue<string>() + " " + e["route"]!.GetValue<string>() == key))
        {
            var min = ClientApiContract.MinSupported(Contract.Value, entry!["client"]!.GetValue<string>());
            var passed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (field, version) in entry["retiredFields"]?.AsObject() ?? [])
            {
                if (min is not null && min.CompareTo(ReleaseVersion.Parse(version!.GetValue<string>())) >= 0)
                {
                    passed.Add(field);
                }
            }

            if (allowed is null)
            {
                allowed = passed;
            }
            else
            {
                allowed.IntersectWith(passed);
            }
        }

        return allowed ?? [];
    }
}
