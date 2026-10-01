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
    private OrderTestKit(
        FakeAppPaths paths, OrderBackend backend, SpoolStore spool, JobJournal journal, FakePrinterService printer, KioskOrderService orders,
        PrintQueueWorker worker)
    {
        Worker = worker;
        Printer = printer;
        Paths = paths;
        Backend = backend;
        Spool = spool;
        Journal = journal;
        Orders = orders;
    }

    public FakeAppPaths Paths { get; }

    public OrderBackend Backend { get; }

    public SpoolStore Spool { get; }

    public FlashUploads FlashUploads { get; private init; } = null!;

    public JobJournal Journal { get; }

    public KioskOrderService Orders { get; }

    public PrintQueueWorker Worker { get; }

    /// <summary>The fake printer: what it takes natively, what it printed.</summary>
    public FakePrinterService Printer { get; }

    public static async Task<OrderTestKit> CreateAsync(
        int paperCurrent = 480, bool blockOnPaper = true, IReadOnlyList<string>? allowedFormats = null, int maxFileSizeMb = 50, bool skipPayment = true)
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

        var uploads = new FlashUploads(api, spool, paths, NullLogger<FlashUploads>.Instance);
        var printer = new FakePrinterService();
        var worker = new PrintQueueWorker(
            new ApiPrintBackend(api), printer, spool, journal, () => FakePrinterService.FakePrinterName,
            NullLogger<PrintQueueWorker>.Instance);

        var orders = new KioskOrderService(
            api,
            worker,
            spool,
            new DocumentPreparer(new PlainConverterRunner(), new ConverterPaths(), NullLogger<DocumentPreparer>.Instance),
            runtime,
            printer,
            Options.Create(new KioskSettings { SkipPayment = skipPayment }),
            uploads,
            NullLogger<KioskOrderService>.Instance);

        return new OrderTestKit(paths, backend, spool, journal, printer, orders, worker) { FlashUploads = uploads };
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

    /// <summary>Payments opened, by id: a test sets their status to answer the polling.</summary>
    public Dictionary<Guid, PaymentInfo> Payments { get; } = [];

    /// <summary>Fiscal receipts by payment; none is a 404.</summary>
    public Dictionary<Guid, FiscalInfo> Fiscal { get; } = [];

    /// <summary>Refunds by job.</summary>
    public Dictionary<Guid, List<RefundInfo>> Refunds { get; } = [];

    /// <summary>(payment, address) for every receipt the kiosk emailed.</summary>
    public List<(Guid PaymentId, string Email)> Emails { get; } = [];

    /// <summary>Jobs whose flash file arrived, and the file's name.</summary>
    public List<(Guid JobId, string FileName)> Uploads { get; } = [];

    /// <summary>Uploads fail as if the network were down.</summary>
    public bool UploadsDown { get; set; }

    /// <summary>Promo codes that exist and what they take off.</summary>
    public Dictionary<string, decimal> Promos { get; } = new(StringComparer.OrdinalIgnoreCase);

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

            using var order = JsonDocument.Parse(Bodies[^1]);
            var dev = order.RootElement.TryGetProperty("dev", out var d) && d.GetBoolean();
            var promo = order.RootElement.TryGetProperty("promoCode", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            if (promo is not null && !Promos.ContainsKey(promo))
            {
                return Problem(HttpStatusCode.BadRequest, """{"title":"Yoxlama","status":400,"errors":{"promoCode":["Belə promokod yoxdur. Kodu yoxlayın."]}}""");
            }

            return Json(Job(id, dev ? "Queued" : "PendingPayment", promoCode: promo, promoDiscount: promo is null ? 0 : Promos[promo]));
        }

        if (path.EndsWith("/api/payments/intents", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var jobId = body.RootElement.GetProperty("jobId").GetGuid();
            var method = body.RootElement.GetProperty("method").GetString()!;

            // The same method again: the open one, as the backend does.
            var open = Payments.Values.FirstOrDefault(p => p.JobId == jobId && p.Method == method && p.IsOpen);
            var paymentId = Guid.NewGuid();
            var payment = open ?? new PaymentInfo(
                paymentId, jobId, "482913", method, "Pending", 0.20m, "AZN",
                method == "BankQr" ? $"MOCKBANK:{paymentId:N}" : $"neoprint://pay/{paymentId}",
                DateTime.UtcNow.AddMinutes(5), 300, null);

            foreach (var other in Payments.Values.Where(p => p.JobId == jobId && p.IsOpen && p.Id != payment.Id).ToList())
            {
                Payments[other.Id] = other with { Status = "Expired", QrContent = null };
            }

            Payments[payment.Id] = payment;
            return Json(payment);
        }

        if (path.StartsWith("/api/payments/", StringComparison.Ordinal) && path.EndsWith("/fiscal", StringComparison.Ordinal))
        {
            var paymentId = Guid.Parse(path.Split('/')[^2]);
            return Fiscal.TryGetValue(paymentId, out var fiscal) ? Json(fiscal) : Problem(HttpStatusCode.NotFound, """{"status":404}""");
        }

        if (path.StartsWith("/api/payments/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        {
            return Payments.TryGetValue(Guid.Parse(path.Split('/')[^1]), out var payment) ? Json(payment) : Problem(HttpStatusCode.NotFound, """{"status":404}""");
        }

        if (path.StartsWith("/api/kiosks/me/receipts/", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Emails.Add((Guid.Parse(path.Split('/')[^2]), body.RootElement.GetProperty("email").GetString()!));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (path.EndsWith("/refunds", StringComparison.Ordinal))
        {
            return Json(Refunds.TryGetValue(Guid.Parse(path.Split('/')[^2]), out var refunds) ? refunds : []);
        }

        if (path.EndsWith("/flash-upload", StringComparison.Ordinal))
        {
            if (UploadsDown)
            {
                throw new HttpRequestException("The network is down.");
            }

            var form = (MultipartFormDataContent)request.Content!;
            Uploads.Add((Guid.Parse(path.Split('/')[^2]), form.First().Headers.ContentDisposition!.FileName!.Trim('"')));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (path.EndsWith("/api/promos/validate", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var code = body.RootElement.GetProperty("code").GetString()!;
            var amount = body.RootElement.GetProperty("amount").GetDecimal();

            return Promos.TryGetValue(code, out var discount)
                ? Json(new PromoValidation(code.ToUpperInvariant(), true, discount, amount - discount, null, null))
                : Json(new PromoValidation(code.ToUpperInvariant(), false, 0, amount, "promos.notFound", "Belə promokod yoxdur. Kodu yoxlayın."));
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

    private static HttpResponseMessage Problem(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
    };

    private static object Job(Guid id, string status, string code = "482913", string? promoCode = null, decimal promoDiscount = 0) => new
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
        price = new { total = 0.20m, currency = "AZN", basePrice = 0.20m, volumeDiscount = 0m, discountPercent = 0m, minimumApplied = false, sides = 2, sheets = 2, profileName = "Standart", promoCode, promoDiscount, amountDue = 0.20m - promoDiscount },
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
