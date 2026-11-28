using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

using NeoPrint.Crypto.Releases;

namespace NeoPrint.Api.IntegrationTests;

// ---------------------------------------------------------------------------
// The clients' API contract (ADR-0009, T-491): what kiosks, provisioning
// scripts and the mobile app already in the field call, so the backend keeps
// serving it. contracts/client-api.json lists it; contracts/client-api.shapes.json
// is what those endpoints take and answer, as generated from the backend.
// ---------------------------------------------------------------------------

/// <summary>One endpoint of the backend: <c>GET api/kiosks/me/config</c>.</summary>
internal sealed record BackendEndpoint(string Method, string Route, MethodInfo Action)
{
    public string Key => Method + " " + Route;
}

/// <summary>A path a client's code calls, as written there.</summary>
internal sealed record ClientUse(string Client, string Path, string? Method, string File, int Line)
{
    public override string ToString() => $"{Client}: {Path} ({File}:{Line})";
}

internal sealed record ClientEvent(string Client, string Name, string File, int Line);

internal static partial class ClientApiContract
{
    public const string UpdateVariable = "NEOPRINT_CONTRACT_UPDATE";

    public static readonly string[] Clients = ["kiosk", "provisioning", "mobile"];

    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string Root { get; } = RepositoryRoot();

    public static string ContractPath => Path.Combine(Root, "contracts", "client-api.json");

    public static string ShapesPath => Path.Combine(Root, "contracts", "client-api.shapes.json");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    // -----------------------------------------------------------------------
    // The routes: normalised so a client's path and a backend template compare.
    // -----------------------------------------------------------------------

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex RouteParameter();

    /// <summary><c>/api/Files/{id:guid}/pages/{page}.png</c> → <c>api/files/{}/pages/{}.png</c>.</summary>
    public static string Normalise(string route)
    {
        var trimmed = route.Trim().TrimStart('~').Trim('/');
        var query = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            trimmed = trimmed[..query];
        }

        return RouteParameter().Replace(trimmed, "{}").ToLowerInvariant();
    }

    /// <summary>A client's segment <c>{}</c> is any value it computes, even one the backend spells out.</summary>
    public static bool Matches(string clientPath, string route)
    {
        var mine = clientPath.Split('/');
        var theirs = route.Split('/');
        if (mine.Length != theirs.Length)
        {
            return false;
        }

        for (var i = 0; i < mine.Length; i++)
        {
            if (mine[i] != theirs[i] && mine[i] != "{}" && theirs[i] != "{}")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The endpoints a client's call reaches: the route closest to how it is
    /// written (<c>api/kiosks/me/queue</c> is that route, not <c>api/kiosks/{id}/queue</c>),
    /// and of its method when the code says it.
    /// </summary>
    public static IReadOnlyList<BackendEndpoint> Served(ClientUse use)
    {
        ArgumentNullException.ThrowIfNull(use);

        var served = BackendEndpoints.Where(endpoint => Matches(use.Path, endpoint.Route)).ToList();
        if (served.Count == 0)
        {
            return served;
        }

        var mine = use.Path.Split('/');
        int Closeness(BackendEndpoint endpoint) => endpoint.Route.Split('/').Where((segment, i) => segment == mine[i]).Count();

        var closest = served.Max(Closeness);
        served = served.Where(endpoint => Closeness(endpoint) == closest).ToList();

        var ofMethod = served.Where(endpoint => endpoint.Method == use.Method).ToList();
        return (ofMethod.Count > 0 ? ofMethod : served).DistinctBy(endpoint => endpoint.Key).ToList();
    }

    // -----------------------------------------------------------------------
    // The backend: its controllers, as MVC routes them.
    // -----------------------------------------------------------------------

    public static IReadOnlyList<BackendEndpoint> BackendEndpoints { get; } = ReadBackend();

    private static List<BackendEndpoint> ReadBackend()
    {
        var endpoints = new List<BackendEndpoint>();
        var controllers = typeof(NeoPrint.Api.Hubs.KioskHub).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

        foreach (var controller in controllers)
        {
            var name = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name[..^"Controller".Length]
                : controller.Name;
            var prefixes = controller.GetCustomAttributes<RouteAttribute>(inherit: true).Select(route => route.Template).ToList();
            if (prefixes.Count == 0)
            {
                prefixes.Add(string.Empty);
            }

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (action.DeclaringType == typeof(ControllerBase) || action.DeclaringType == typeof(object)
                    || action.IsDefined(typeof(NonActionAttribute)))
                {
                    continue;
                }

                var verbs = action.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToList();
                var actionRoutes = action.GetCustomAttributes<RouteAttribute>(inherit: true).Select(route => route.Template).ToList();

                foreach (var verb in verbs)
                {
                    var templates = verb.Template is not null ? [verb.Template] : actionRoutes.Count > 0 ? actionRoutes : [string.Empty];
                    foreach (var template in templates)
                    {
                        foreach (var prefix in prefixes)
                        {
                            var full = template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)
                                ? template
                                : string.IsNullOrEmpty(template) ? prefix : prefix.TrimEnd('/') + "/" + template;
                            full = full.Replace("[controller]", name, StringComparison.OrdinalIgnoreCase)
                                .Replace("[action]", action.Name, StringComparison.OrdinalIgnoreCase);

                            foreach (var method in verb.HttpMethods)
                            {
                                endpoints.Add(new BackendEndpoint(method.ToUpperInvariant(), Normalise(full), action));
                            }
                        }
                    }
                }
            }
        }

        return endpoints;
    }

    /// <summary>Every string the backend's own code holds: a hub event is sent by its name.</summary>
    public static string BackendSource { get; } = string.Join(
        '\n',
        Directory.EnumerateFiles(Path.Combine(Root, "backend", "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

    // -----------------------------------------------------------------------
    // The clients: the paths their code calls.
    // -----------------------------------------------------------------------

    [GeneratedRegex(@"HttpMethod\.(Get|Post|Put|Delete|Patch)\b|\b(Get|Post|Put|Delete|Patch)(?:AsJson|FromJson)?Async\b|method:\s*'(GET|POST|PUT|DELETE|PATCH)'|\.(get|post|put|delete|patch)\s*[<(]|-Method\s+(Get|Post|Put|Delete|Patch)\b|-X\s+(GET|POST|PUT|DELETE|PATCH)\b")]
    private static partial Regex MethodHint();

    [GeneratedRegex("""\.On(?:<[^>]*>)?\("([A-Za-z]+)"[,)]""")]
    private static partial Regex KioskEvent();

    [GeneratedRegex(@"target\s*==\s*'([A-Z][A-Za-z]+)'|case\s+'([A-Z][A-Za-z]+)'(?:\s+when|\s*:)")]
    private static partial Regex MobileEvent();

    public static IReadOnlyList<ClientUse> ClientUses { get; } = ReadClients();

    public static IReadOnlyList<ClientEvent> ClientEvents { get; } = ReadClientEvents();

    private static IEnumerable<(string File, string[] Lines)> Sources(string client) => client switch
    {
        "kiosk" => Files(Path.Combine("desktop", "src"), "*.cs"),
        "provisioning" => Files(Path.Combine("desktop", "provisioning"), "*.sh").Concat(Files(Path.Combine("desktop", "provisioning"), "*.ps1")),
        "mobile" => Files(Path.Combine("mobile", "lib"), "*.dart"),
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
    };

    private static IEnumerable<(string File, string[] Lines)> Files(string folder, string pattern)
        => Directory.EnumerateFiles(Path.Combine(Root, folder), pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(file => (Path.GetRelativePath(Root, file).Replace('\\', '/'), File.ReadAllLines(file)));

    private static List<ClientUse> ReadClients()
    {
        var uses = new List<ClientUse>();
        foreach (var client in Clients)
        {
            foreach (var (file, lines) in Sources(client))
            {
                var inHelp = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();

                    // PowerShell's help block, <# … #>: words, not calls.
                    if (trimmed.StartsWith("<#", StringComparison.Ordinal))
                    {
                        inHelp = !trimmed.Contains("#>", StringComparison.Ordinal);
                        continue;
                    }

                    if (inHelp)
                    {
                        inHelp = !trimmed.Contains("#>", StringComparison.Ordinal);
                        continue;
                    }

                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith('#'))
                    {
                        continue;
                    }

                    foreach (var path in PathsIn(lines[i], client))
                    {
                        uses.Add(new ClientUse(client, path, MethodNear(lines, i), file, i + 1));
                    }
                }
            }
        }

        return uses;
    }

    private static List<ClientEvent> ReadClientEvents()
    {
        var events = new List<ClientEvent>();
        foreach (var (file, lines) in Sources("kiosk"))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in KioskEvent().Matches(lines[i]))
                {
                    events.Add(new ClientEvent("kiosk", match.Groups[1].Value, file, i + 1));
                }
            }
        }

        foreach (var (file, lines) in Sources("mobile"))
        {
            // Only the code that reads hub events: a "case 'Foo':" elsewhere is something else.
            if (!lines.Any(line => line.Contains(".target", StringComparison.Ordinal) || line.Contains("HubEvent", StringComparison.Ordinal)))
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in MobileEvent().Matches(lines[i]))
                {
                    var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    events.Add(new ClientEvent("mobile", name, file, i + 1));
                }
            }
        }

        return events;
    }

    /// <summary>The method nearest above a path - for a new contract row only; the row is what counts.</summary>
    private static string? MethodNear(string[] lines, int index)
    {
        for (var i = index; i >= Math.Max(0, index - 4); i--)
        {
            var matches = MethodHint().Matches(lines[i]);
            if (matches.Count > 0)
            {
                var match = matches[^1];
                var verb = match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value;
                return verb.ToUpperInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>api/…</c> paths in a line, each computed part (<c>{jobId}</c>,
    /// <c>$id</c>, <c>${x}</c>, <c>$(x)</c>) as <c>{}</c>.
    /// </summary>
    internal static IEnumerable<string> PathsIn(string line, string client)
    {
        var start = 0;
        while ((start = line.IndexOf("api/", start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 ? ' ' : line[start - 1];
            var at = start;
            start += 4;

            // A path begins a string or follows a slash: "api/…", '/api/…', …/api/….
            if (before is not ('"' or '\'' or '/' or '`' or ' ' or '='))
            {
                continue;
            }

            var path = new StringBuilder();
            var i = at;
            while (i < line.Length)
            {
                var c = line[i];
                if (c is '"' or '\'' or '`' or '?' or ' ' or '\t' or ')' or ',' or ';' or '&' or '#')
                {
                    break;
                }

                if (c == '{' && client == "kiosk")
                {
                    i = SkipBraces(line, i);
                    path.Append("{}");
                    continue;
                }

                if (c == '$')
                {
                    if (i + 1 < line.Length && line[i + 1] is '{' or '(')
                    {
                        i = line[i + 1] == '{' ? SkipBraces(line, i + 1) : SkipParentheses(line, i + 1);
                    }
                    else
                    {
                        i++;
                        while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] == '_'))
                        {
                            i++;
                        }
                    }

                    path.Append("{}");
                    continue;
                }

                if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '{' or '}'))
                {
                    break;
                }

                path.Append(c);
                i++;
            }

            var text = path.ToString().TrimEnd('.');
            if (text.EndsWith('/'))
            {
                // "api/print-jobs/" + id: a part is added after the string.
                text += "{}";
            }

            if (text.Length > "api/".Length)
            {
                yield return Normalise(text);
            }

            start = Math.Max(start, i);
        }
    }

    private static int SkipBraces(string line, int open)
    {
        var depth = 0;
        for (var i = open; i < line.Length; i++)
        {
            depth += line[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return i + 1;
            }
        }

        return line.Length;
    }

    private static int SkipParentheses(string line, int open)
    {
        var depth = 0;
        for (var i = open; i < line.Length; i++)
        {
            depth += line[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i + 1;
            }
        }

        return line.Length;
    }

    // -----------------------------------------------------------------------
    // The contract file.
    // -----------------------------------------------------------------------

    public static JsonObject LoadContract() => (JsonObject)JsonNode.Parse(File.ReadAllText(ContractPath))!;

    public static void Save(string path, JsonNode node)
        => File.WriteAllText(path, node.ToJsonString(Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

    public static ReleaseVersion? MinSupported(JsonObject contract, string client)
        => contract["minSupported"]?[client]?.GetValue<string?>() is { } text ? ReleaseVersion.Parse(text) : null;

    /// <summary>Still called by a client the backend supports: no supported version has stopped.</summary>
    public static bool IsActive(JsonObject contract, JsonNode entry)
    {
        if (entry["retiredIn"]?.GetValue<string>() is not { } retired)
        {
            return true;
        }

        var min = MinSupported(contract, entry["client"]!.GetValue<string>());
        return min is null || min.CompareTo(ReleaseVersion.Parse(retired)) < 0;
    }

    // -----------------------------------------------------------------------
    // Shapes: what an endpoint takes and answers, as the JSON options write it.
    // -----------------------------------------------------------------------

    private static readonly NullabilityInfoContext Nullability = new();

    public static JsonObject ShapeOf(BackendEndpoint endpoint)
    {
        var shape = new JsonObject();
        var body = endpoint.Action.GetParameters().FirstOrDefault(parameter => parameter.IsDefined(typeof(FromBodyAttribute)));
        if (body is not null)
        {
            shape["request"] = Describe(body.ParameterType, Nullability.Create(body), [], request: true);
        }

        shape["response"] = ResponseOf(endpoint.Action);
        return shape;
    }

    private static JsonNode ResponseOf(MethodInfo action)
    {
        var produced = action.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .FirstOrDefault(attribute => attribute.StatusCode is >= 200 and < 300 && attribute.Type != typeof(void));
        if (produced is not null)
        {
            return Describe(produced.Type, null, [], request: false);
        }

        var type = action.ReturnType;
        NullabilityInfo? nullability = Nullability.Create(action.ReturnParameter);
        while (true)
        {
            if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>)
                || type.GetGenericTypeDefinition() == typeof(ActionResult<>)))
            {
                type = type.GetGenericArguments()[0];
                nullability = nullability?.GenericTypeArguments.FirstOrDefault();
                continue;
            }

            break;
        }

        if (type == typeof(Task) || type == typeof(void) || type == typeof(ValueTask))
        {
            return "none";
        }

        if (typeof(IActionResult).IsAssignableFrom(type) || typeof(Microsoft.AspNetCore.Http.IResult).IsAssignableFrom(type))
        {
            return typeof(FileResult).IsAssignableFrom(type) ? "file" : "untyped";
        }

        return Describe(type, nullability, [], request: false);
    }

    private static JsonNode Describe(Type type, NullabilityInfo? nullability, HashSet<Type> path, bool request)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var nullable = underlying is not null || (!type.IsValueType && nullability?.ReadState == NullabilityState.Nullable);
        type = underlying ?? type;
        var suffix = nullable ? "?" : string.Empty;

        if (Scalar(type) is { } scalar)
        {
            return scalar + suffix;
        }

        if (type.IsEnum)
        {
            return "enum" + suffix + "(" + string.Join('|', Enum.GetNames(type).Order(StringComparer.Ordinal)) + ")";
        }

        if (type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(JsonObject) || type == typeof(object))
        {
            return "json" + suffix;
        }

        if (typeof(Stream).IsAssignableFrom(type) || type == typeof(byte[]))
        {
            return "binary" + suffix;
        }

        var dictionary = type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary is not null)
        {
            return new JsonObject
            {
                ["map" + suffix] = Describe(dictionary.GetGenericArguments()[1], nullability?.GenericTypeArguments.ElementAtOrDefault(1), path, request),
            };
        }

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsArray
                ? type.GetElementType()!
                : type.GetInterfaces().Append(type).First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)).GetGenericArguments()[0];
            var elementNullability = type.IsArray ? nullability?.ElementType : nullability?.GenericTypeArguments.FirstOrDefault();
            return new JsonObject { ["list" + suffix] = Describe(element, elementNullability, path, request) };
        }

        if (!path.Add(type) || path.Count > 12)
        {
            return "ref(" + type.Name + ")" + suffix;
        }

        var shape = new JsonObject();
        if (nullable)
        {
            shape["?"] = true;
        }

        var required = request ? RequiredOf(type) : [];
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0
                         && !property.IsDefined(typeof(JsonIgnoreAttribute)))
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var described = Describe(property.PropertyType, Nullability.Create(property), path, request);
            shape[required.Contains(property.Name) ? name + "!" : name] = described;
        }

        path.Remove(type);
        return shape;
    }

    /// <summary>What a request cannot leave out: a constructor parameter with no default, or a <c>required</c> member.</summary>
    private static HashSet<string> RequiredOf(Type type)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        foreach (var parameter in constructor?.GetParameters() ?? [])
        {
            var nullable = Nullable.GetUnderlyingType(parameter.ParameterType) is not null
                || (!parameter.ParameterType.IsValueType && Nullability.Create(parameter).WriteState == NullabilityState.Nullable);
            if (!parameter.HasDefaultValue && !nullable && parameter.Name is not null)
            {
                required.Add(parameter.Name);
            }
        }

        foreach (var property in type.GetProperties().Where(p => p.IsDefined(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute))))
        {
            required.Add(property.Name);
        }

        return required;
    }

    private static string? Scalar(Type type) => type switch
    {
        _ when type == typeof(string) => "string",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) => "integer",
        _ when type == typeof(decimal) || type == typeof(double) || type == typeof(float) => "number",
        _ when type == typeof(Guid) => "guid",
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "datetime",
        _ when type == typeof(DateOnly) => "date",
        _ when type == typeof(TimeOnly) || type == typeof(TimeSpan) => "time",
        _ when type == typeof(Uri) => "string",
        _ => null,
    };

    /// <summary>
    /// What a client of the old shape would trip on. A response field gone or
    /// changed, a request field it does not send now required; a new response
    /// field, a new enum value or a field it may leave out does not break it.
    /// </summary>
    public static void Compare(JsonNode? before, JsonNode? now, string at, bool request, ISet<string> allowed, List<string> breaking)
    {
        if (before is null)
        {
            return;
        }

        if (now is null)
        {
            if (!request && !allowed.Contains(at))
            {
                breaking.Add($"{at}: removed");
            }

            return;
        }

        if (before is JsonValue && now is JsonValue)
        {
            var was = before.GetValue<string>();
            var current = now.GetValue<string>();
            if (was == current)
            {
                return;
            }

            var wasNullable = was.StartsWith("enum?", StringComparison.Ordinal) || was.EndsWith('?');
            var nowNullable = current.StartsWith("enum?", StringComparison.Ordinal) || current.EndsWith('?');

            if (was.StartsWith("enum", StringComparison.Ordinal) && current.StartsWith("enum", StringComparison.Ordinal))
            {
                // A response may gain values (ADR-0009: a client takes an unknown one);
                // none may go - an old client sends or expects them.
                var gone = EnumValues(was).Except(EnumValues(current)).ToList();
                if (gone.Count > 0)
                {
                    breaking.Add($"{at}: enum values removed ({string.Join(", ", gone)})");
                }
            }
            else if (was.TrimEnd('?') != current.TrimEnd('?'))
            {
                breaking.Add($"{at}: {was} → {current}");
                return;
            }

            if (!request && nowNullable && !wasNullable)
            {
                breaking.Add($"{at}: may now be null, an old client expects a value");
            }
            else if (request && wasNullable && !nowNullable)
            {
                breaking.Add($"{at}: may no longer be null, an old client sends null");
            }

            return;
        }

        if (before is JsonObject oldShape && now is JsonObject newShape)
        {
            if (!request && !oldShape.ContainsKey("?") && newShape.ContainsKey("?"))
            {
                breaking.Add($"{at}: may now be null");
            }

            var oldFields = oldShape.Where(p => p.Key != "?").ToDictionary(p => p.Key.TrimEnd('!'), p => (Required: p.Key.EndsWith('!'), Node: p.Value), StringComparer.Ordinal);
            var newFields = newShape.Where(p => p.Key != "?").ToDictionary(p => p.Key.TrimEnd('!'), p => (Required: p.Key.EndsWith('!'), Node: p.Value), StringComparer.Ordinal);

            foreach (var (name, field) in oldFields)
            {
                var child = at.Length == 0 ? name : at + "." + name;
                if (!newFields.TryGetValue(name, out var nowField))
                {
                    if (!request && !allowed.Contains(child))
                    {
                        breaking.Add($"{child}: removed");
                    }

                    continue;
                }

                if (request && nowField.Required && !field.Required)
                {
                    breaking.Add($"{child}: now required, an old client may not send it");
                }

                Compare(field.Node, nowField.Node, child, request, allowed, breaking);
            }

            if (request)
            {
                foreach (var (name, field) in newFields.Where(f => f.Value.Required && !oldFields.ContainsKey(f.Key)))
                {
                    breaking.Add($"{(at.Length == 0 ? name : at + "." + name)}: a new required field, an old client does not send it");
                }
            }

            return;
        }

        breaking.Add($"{at}: {Describe(before)} → {Describe(now)}");
    }

    private static string Describe(JsonNode node) => node is JsonValue ? node.GetValue<string>() : node.ToJsonString();

    private static string[] EnumValues(string descriptor)
    {
        var open = descriptor.IndexOf('(', StringComparison.Ordinal);
        return descriptor[(open + 1)..^1].Split('|');
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root is not above the test output.");
    }

    public static string Lines(this IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);
}
