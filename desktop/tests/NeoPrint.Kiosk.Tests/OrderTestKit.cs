using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Printing;

using PdfSharp.Pdf;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// A kiosk that takes flash orders, on a temporary folder, against a backend
/// answered by hand (plan section 11: no mocking library).
/// </summary>
internal sealed class OrderTestKit : IDisposable
{
    private OrderTestKit(FakeAppPaths paths, OrderBackend backend, SpoolStore spool, JobJournal journal, KioskOrderService orders)
    {
        Paths = paths;
        Backend = backend;
        Spool = spool;
        Journal = journal;
        Orders = orders;
    }

    public FakeAppPaths Paths { get; }

    public OrderBackend Backend { get; }

    public SpoolStore Spool { get; }

    public JobJournal Journal { get; }

    public KioskOrderService Orders { get; }

    public static async Task<OrderTestKit> CreateAsync(
        int paperCurrent = 480, bool blockOnPaper = true, IReadOnlyList<string>? allowedFormats = null, int maxFileSizeMb = 50)
    {
        var paths = new FakeAppPaths();
        paths.EnsureCreated();

        var backend = new OrderBackend();
        var spool = new SpoolStore(paths, new FakeSpoolKeyStore(paths));
        var journal = new JobJournal(paths);

        var http = new HttpClient(backend) { BaseAddress = new Uri("http://backend.test/") };
        var api = new KioskApiClient(http, new FakeDeviceKeyStore(paths), NullLogger<KioskApiClient>.Instance);

        await api.RegisterAsync(
            new MachineFingerprint("test-host", "Linux", "6.1", "machine-1", [], "CPU", 8, "1920x1080"),
            "Linux",
            "0.1.0",
            enrollmentToken: null,
            printers: []);

        // The configuration comes from the disk cache: the quote needs no call.
        var cache = new ConfigurationCache(paths);
        cache.Save(Configuration(paperCurrent, blockOnPaper, allowedFormats, maxFileSizeMb), DateTime.UtcNow);

        var runtime = new KioskRuntime(api, new SystemClock(), NullLogger<KioskRuntime>.Instance, cache);
        runtime.LoadCachedConfiguration().Should().BeTrue();

        var worker = new PrintQueueWorker(
            new ApiPrintBackend(api), new FakePrinterService(), spool, journal, () => FakePrinterService.FakePrinterName,
            NullLogger<PrintQueueWorker>.Instance);

        var orders = new KioskOrderService(
            api,
            worker,
            spool,
            new DocumentPreparer(new PlainConverterRunner(), new ConverterPaths(), NullLogger<DocumentPreparer>.Instance),
            runtime,
            Options.Create(new KioskSettings { SkipPayment = true }),
            NullLogger<KioskOrderService>.Instance);

        return new OrderTestKit(paths, backend, spool, journal, orders);
    }

    public void Dispose()
    {
        if (Directory.Exists(Paths.Root))
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
    }

    /// <summary>A PDF of blank pages under <paramref name="folder"/> (the kit's root by default).</summary>
    public string Pdf(int pages, string? folder = null, string? name = null)
    {
        var path = Path.Combine(folder ?? Paths.Root, name ?? $"{Guid.NewGuid():N}.pdf");

        using var document = new PdfDocument();
        for (var page = 0; page < pages; page++)
        {
            document.AddPage();
        }

        document.Save(path);
        return path;
    }

    private static KioskConfiguration Configuration(
        int paperCurrent, bool blockOnPaper, IReadOnlyList<string>? allowedFormats, int maxFileSizeMb) => new(
        Guid.NewGuid(),
        "KSK-0001",
        "Mərkəz",
        "Online",
        "az",
        FakePrinterService.FakePrinterName,
        new PrinterCapabilityFlags(true, true, true),
        [],
        null,
        5,
        maxFileSizeMb,
        200,
        null,
        new Dictionary<string, PaperStockLevel> { ["A4"] = new(500, paperCurrent) },
        new Dictionary<string, bool>(),
        15,
        24,
        DateTime.UtcNow,
        new KioskPrices("Standart", 0.10m, 0.50m, new Dictionary<string, decimal> { ["A4"] = 1m, ["A3"] = 2m }, 0m, [], "AZN"),
        allowedFormats,
        PaperBlockWhenInsufficient: blockOnPaper);
}

/// <summary>The backend's print-job endpoints, as the kiosk calls them.</summary>
internal sealed class OrderBackend : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Guid> _byKey = [];

    public List<string?> IdempotencyKeys { get; } = [];

    public List<string> Bodies { get; } = [];

    public List<Guid> Cancelled { get; } = [];

    /// <summary>The next order request fails as if the connection dropped.</summary>
    public bool DropNextOrder { get; set; }

    /// <summary>What <c>by-code</c> finds, by code; anything else is 404.</summary>
    public Dictionary<string, string> JobsByCode { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("/register", StringComparison.Ordinal))
        {
            return Json(new
            {
                deviceId = Guid.NewGuid(),
                activated = true,
                pairingCode = (string?)null,
                pairingCodeExpiresAt = (DateTime?)null,
                kioskId = Guid.NewGuid(),
                kioskCode = "KSK-0001",
            });
        }

        if (path.EndsWith("/token", StringComparison.Ordinal) || path.EndsWith("/challenge", StringComparison.Ordinal))
        {
            return Json(new { accessToken = "kiosk-token", expiresAt = DateTime.UtcNow.AddHours(1), tokenType = "kiosk", nonce = "nonce" });
        }

        if (path.EndsWith("/api/print-jobs", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
            IdempotencyKeys.Add(key);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            // The same key returns the same job, as the backend's filter does.
            var id = key is not null && _byKey.TryGetValue(key, out var existing) ? existing : Guid.NewGuid();

            if (key is not null)
            {
                _byKey[key] = id;
            }

            if (DropNextOrder)
            {
                // Made on the server, lost on the way back.
                DropNextOrder = false;
                throw new HttpRequestException("The connection was reset.");
            }

            return Json(Job(id, "Queued"));
        }

        if (path.Contains("/api/print-jobs/by-code/", StringComparison.Ordinal))
        {
            var code = path.Split('/')[^1];

            return JobsByCode.TryGetValue(code, out var status)
                ? Json(Job(Guid.NewGuid(), status, code))
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{"title":"Sifariş tapılmadı.","status":404,"code":"printJobs.notFound"}""",
                        Encoding.UTF8,
                        "application/problem+json"),
                };
        }

        if (path.EndsWith("/cancel", StringComparison.Ordinal))
        {
            var id = Guid.Parse(path.Split('/')[^2]);
            Cancelled.Add(id);
            return Json(Job(id, "Cancelled"));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static object Job(Guid id, string status, string code = "482913") => new
    {
        id,
        jobCode = code,
        kioskId = Guid.NewGuid(),
        status,
        source = "Flash",
        fileName = "report.pdf",
        pageCount = 4,
        selectedPages = 2,
        options = new JobOptions(PageRange: "2-3"),
        price = new { total = 0.20m, currency = "AZN", basePrice = 0.20m, volumeDiscount = 0m, discountPercent = 0m, minimumApplied = false, sides = 2, sheets = 2, profileName = "Standart" },
        requiredSheets = 2,
        queuePosition = (int?)1,
        printedSides = 0,
        printedSheets = 0,
        errorReason = (string?)null,
        cancelReason = (string?)null,
        paymentSkipped = true,
        createdAt = DateTime.UtcNow,
    };

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json"),
    };
}
