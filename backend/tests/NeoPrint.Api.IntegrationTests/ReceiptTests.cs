using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Fiscal;
using NeoPrint.Application.Receipts;
using NeoPrint.Contracts;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Domain.Receipts;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Receipts;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// NeoPrint's own receipts (plan section 2.12; docs/TASKS.md T-106): one per
/// payment, numbered, stored encrypted, sent by email and re-made when the
/// fiscal receipt arrives.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReceiptTests
{
    private readonly NeoPrintApiFixture _fixture;

    public ReceiptTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_payment_gets_one_numbered_receipt_that_shows_its_fiscal_receipt()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "receipt-one");
        var (_, first, _) = await PaidBankJobAsync(_fixture, kiosk);
        var (_, second, _) = await PaidBankJobAsync(_fixture, kiosk);

        var receipt = await EventuallyAsync(() => ReceiptAsync(first), r => r is { FiscalShown: true, Pdf: not null }, "the receipt with its fiscal part");
        var next = await EventuallyAsync(() => ReceiptAsync(second), r => r?.Pdf is not null, "the second receipt");

        receipt!.Id.Should().Be(first, "a receipt's id is its payment's");
        receipt.Number.Should().MatchRegex($"^NP-{DateTime.UtcNow.Year}-\\d{{6}}$");
        next!.Number.Should().NotBe(receipt.Number);

        // The event again, twice at once: still one receipt with the same number.
        var again = new PaymentSucceeded(first, null, kiosk.KioskId, null, 0.30m, "BankQr", true, null);
        await Task.WhenAll(CreateAsync(again), CreateAsync(again));

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        (await context.Receipts.Find(r => r.PaymentId == first).ToListAsync()).Should().ContainSingle().Which.Number.Should().Be(receipt.Number);

        // Stored encrypted: what the store holds is not a PDF.
        receipt.Pdf!.PlaintextLength.Should().BeGreaterThan(1000);
        receipt.Pdf.EncryptedLength.Should().BeGreaterThan(receipt.Pdf.PlaintextLength);
    }

    [DockerFact]
    public async Task The_kiosk_emails_its_fresh_receipt_and_the_fiscal_version_follows_when_it_is_issued()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "receipt-mail");
        using var other = await ReadyKioskAsync(_fixture, "receipt-other");
        using var anonymous = _fixture.CreateApiClient();
        var address = $"receipt-{Guid.NewGuid():N}@neoprint.test";
        Guid paymentId;

        (await anonymous.PostAsync("/api/dev/mock-fiscal/mode?mode=Down", null)).EnsureSuccessStatusCode();

        try
        {
            (_, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);
            await EventuallyAsync(() => ReceiptAsync(paymentId), r => r?.Pdf is not null, "the receipt");

            (await EmailAsync(other.Client, paymentId, address)).StatusCode.Should().Be(HttpStatusCode.NotFound, "only the kiosk that took the payment");
            (await EmailAsync(kiosk.Client, paymentId, "not-an-address")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var sent = await EmailAsync(kiosk.Client, paymentId, address);
            sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());

            var first = _fixture.Emails.Latest(address);
            first!.Attachments.Should().ContainSingle().Which.Content.Take(4).Should().Equal(Encoding.ASCII.GetBytes("%PDF"));
            (await ReceiptAsync(paymentId))!.FiscalShown.Should().BeFalse("the e-kassa is down: \"Fiskal çek hazırlanır\"");
        }
        finally
        {
            (await anonymous.PostAsync("/api/dev/mock-fiscal/mode?mode=Ok", null)).EnsureSuccessStatusCode();
        }

        // The e-kassa is back: the receipt is re-made and goes to the same address again.
        var sale = await ReceiptBySaleAsync(paymentId);
        await SendAsync(new IssueFiscalReceiptCommand(sale.Id), makeDue: sale.Id);

        await EventuallyAsync(() => ReceiptAsync(paymentId), r => r?.FiscalShown == true, "the fiscal version");
        await EventuallyAsync(() => Task.FromResult(_fixture.Emails.Sent.Count(e => e.To == address)), n => n == 2, "the second email");

        // Never more than a handful of addresses.
        for (var i = 1; i < Receipt.MaxEmails; i++)
        {
            (await EmailAsync(kiosk.Client, paymentId, $"r{i}-{Guid.NewGuid():N}@neoprint.test")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await EmailAsync(kiosk.Client, paymentId, $"late-{Guid.NewGuid():N}@neoprint.test")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task An_account_sees_only_its_own_receipts_and_an_admin_finds_them_by_number()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);

        using (var topUp = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/top-up") { Content = JsonContent.Create(new { amount = 5m }) })
        {
            topUp.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await user.Client.SendAsync(topUp)).EnsureSuccessStatusCode();
        }

        var list = await EventuallyAsync(
            async () => await user.Client.GetFromJsonAsync<JsonElement>("/api/receipts"), l => l.GetArrayLength() == 1, "the top-up receipt");
        var mine = list[0];
        var id = mine.GetProperty("id").GetGuid();
        mine.GetProperty("purpose").GetString().Should().Be("WalletTopUp");
        mine.GetProperty("amount").GetDecimal().Should().Be(5m);

        var pdf = await user.Client.GetAsync($"/api/receipts/{id}/pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

        (await stranger.Client.GetAsync($"/api/receipts/{id}/pdf")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.Client.GetFromJsonAsync<JsonElement>("/api/receipts")).GetArrayLength().Should().Be(0);

        // To the account's own confirmed address when none is given.
        using (var email = new HttpRequestMessage(HttpMethod.Post, $"/api/receipts/{id}/email") { Content = JsonContent.Create(new { }) })
        {
            email.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await user.Client.SendAsync(email)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        _fixture.Emails.Latest(user.Email)!.Attachments.Should().ContainSingle();

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var number = mine.GetProperty("number").GetString();
        var found = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/receipts?number={number}");
        found.GetProperty("items").EnumerateArray().Should().ContainSingle(item => item.GetProperty("id").GetGuid() == id);
        (await admin.GetAsync($"/api/admin/receipts/{id}/pdf")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var resend = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/receipts/{id}/resend");
        resend.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await admin.SendAsync(resend)).StatusCode.Should().Be(HttpStatusCode.OK);
        _fixture.Emails.Sent.Count(e => e.To == user.Email && e.Attachments is { Count: 1 }).Should().Be(2);
    }

    [DockerFact]
    public async Task A_stored_pdf_that_cannot_be_read_is_made_again()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "receipt-lost");
        var (_, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);
        var receipt = await EventuallyAsync(() => ReceiptAsync(paymentId), r => r?.Pdf is not null, "the receipt");

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Receipts.UpdateOneAsync(
                Builders<Receipt>.Filter.Eq(r => r.Id, paymentId),
                Builders<Receipt>.Update.Set("Pdf.Key", $"receipts/missing/{Guid.NewGuid():N}.pdf"));
        }

        var pdf = await SendAsync(new GetReceiptPdfQuery(paymentId, UserId: null));
        pdf.Content.Take(4).Should().Equal(Encoding.ASCII.GetBytes("%PDF"));
        (await ReceiptAsync(paymentId))!.Pdf!.Key.Should().Be(receipt!.Pdf!.Key, "rendered again and stored under its own key");
    }

    private async Task CreateAsync(PaymentSucceeded payment)
    {
        try
        {
            await SendAsync(new CreateReceiptCommand(payment));
        }
        catch (MongoException)
        {
            // The racing twin lost at the primary key.
        }
    }

    private async Task<T> SendAsync<T>(IRequest<T> request, Guid? makeDue = null)
    {
        using var scope = _fixture.Services.CreateScope();

        if (makeDue is { } receiptId)
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().FiscalReceipts.UpdateOneAsync(
                Builders<FiscalReceipt>.Filter.Eq(r => r.Id, receiptId), Builders<FiscalReceipt>.Update.Set(r => r.NextAttemptAt, DateTime.UtcNow));
        }

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task<Receipt?> ReceiptAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IReceiptRepository>().FindByIdAsync(paymentId);
    }

    private async Task<FiscalReceipt> ReceiptBySaleAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IFiscalReceiptRepository>().FindByDedupKeyAsync(FiscalizationHandlers.SaleKey(paymentId)))!;
    }

    private static async Task<HttpResponseMessage> EmailAsync(HttpClient kiosk, Guid receiptId, string email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/kiosks/me/receipts/{receiptId}/email") { Content = JsonContent.Create(new { email }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await kiosk.SendAsync(request);
    }
}

/// <summary>The PDF itself, without the stack: every language's letters are in the font, money is written as the design system says.</summary>
public sealed class ReceiptRendererTests
{
    [Theory]
    [InlineData("az")]
    [InlineData("en")]
    [InlineData("ru")]
    public void Every_language_renders_with_its_letters_and_the_manat_sign(string language)
    {
        var labels = new Dictionary<string, string>
        {
            ["receipt.title"] = "Qəbz Квитанция",
            ["receipt.fiscal.pending"] = "Fiskal çek hazırlanır — ƏĞİÖÜŞÇ əğıöüşç ЁЖЩ ₼",
        };

        var content = new ReceiptContent(
            language,
            "NP-2026-000001",
            new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc),
            "KSK-9001",
            "Bakı, 28 May",
            "123456",
            "hesabat-şəkil.pdf",
            "3 səhifə",
            [new ReceiptLine("Çap: 3 üz × 0,10 ₼", 0.30m)],
            1240.5m,
            "AZN",
            [new ReceiptLine("Bank QR", 0.30m)],
            0,
            new ReceiptFiscal(false, null, null, null, null),
            null,
            labels);

        // ThrowOnMissingTextGlyphs: a letter the font lacks fails here, not on paper.
        var pdf = new QuestPdfReceiptRenderer().Render(content);

        pdf.Take(4).Should().Equal(Encoding.ASCII.GetBytes("%PDF"));
        QuestPDF.Drawing.FontManager.GetRegisteredFonts().Select(font => font.FamilyName).Should().Contain("Inter", "the design system's font, not a fallback");
    }

    [Theory]
    [InlineData("az", "1 240,50 ₼")]
    [InlineData("ru", "1 240,50 ₼")]
    [InlineData("en", "1,240.50 ₼")]
    public void Money_is_written_as_the_design_system_says(string language, string expected)
        => QuestPdfReceiptRenderer.Money(1240.5m, language).Should().Be(expected);
}
