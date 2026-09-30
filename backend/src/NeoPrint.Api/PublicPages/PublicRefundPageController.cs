using System.Globalization;
using System.Net;
using System.Text;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Refunds;
using NeoPrint.Application.Support;
using NeoPrint.Domain.Common;

namespace NeoPrint.Api.PublicPages;

/// <summary>
/// The refund status page the kiosk's QR opens (plan section 2.9:
/// <c>{site}/refund/{id}</c>; docs/DESIGN_SYSTEM.md §5.6): status, amount,
/// where it goes, and "Problem barədə bildir". Served by the API itself - no
/// separate site to deploy - with no personal data and no caching.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class PublicRefundPageController : ControllerBase
{
    private static readonly string[] Languages = ["az", "en", "ru"];

    private readonly ISender _sender;

    public PublicRefundPageController(ISender sender) => _sender = sender;

    [HttpGet("/refund/{id:guid}")]
    public async Task<ContentResult> Page(Guid id, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        var language = Language(lang);
        string T(string key, params (string Name, object? Value)[] arguments) => Messages.Write(UserMessage.Of(key, arguments), language);

        Response.Headers.CacheControl = "no-store";

        PublicRefundView refund;

        try
        {
            refund = await _sender.Send(new GetPublicRefundQuery(id), cancellationToken);
        }
        catch (NotFoundException)
        {
            return Html(HttpStatusCode.NotFound, language, T("page.refund.title"), $"""
                <section class="card status status--danger">
                  <p class="status__line"><span class="status__icon" aria-hidden="true">!</span> {E(T("page.refund.notFound"))}</p>
                  <p>{E(T("page.refund.notFoundHelp"))}</p>
                </section>
                {LanguageLinks(id, language)}
                """);
        }

        var (tone, icon) = refund.Status switch
        {
            "Succeeded" => ("success", "✓"),
            "Failed" => ("danger", "!"),
            _ => ("info", "…"),
        };

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"""
            <section class="card status status--{tone}" aria-live="polite">
              <p class="status__line"><span class="status__icon" aria-hidden="true">{icon}</span> {E(T($"page.refund.status.{Key(refund.Status)}"))}</p>
              <p class="amount">{E(Money(refund.Amount, language))}</p>
              <p>{E(T($"page.refund.to.{Key(refund.Destination)}"))}</p>
              <dl>
                <dt>{E(T("page.refund.created"))}</dt><dd>{E(Date(refund.CreatedAt))}</dd>
            """);

        if (refund.CompletedAt is { } completed)
        {
            body.Append(CultureInfo.InvariantCulture, $"<dt>{E(T("page.refund.completed"))}</dt><dd>{E(Date(completed))}</dd>");
        }

        body.Append("</dl>");
        body.Append(CultureInfo.InvariantCulture, $"<p class=\"hint\">{E(T($"page.refund.explain.{Key(refund.Destination)}.{Key(refund.Status)}"))}</p>");

        if (refund.VoucherCode is { } voucher)
        {
            body.Append(CultureInfo.InvariantCulture, $"<p class=\"voucher\">{E(voucher)}</p>");
        }

        body.Append("</section>");
        body.Append(CultureInfo.InvariantCulture, $$"""
            <section class="card">
              <h2>{{E(T("page.refund.claimTitle"))}}</h2>
              <p class="hint">{{E(T("page.refund.claimHelp"))}}</p>
              <form id="claim">
                <label for="text">{{E(T("page.refund.claimText"))}}</label>
                <textarea id="text" name="text" maxlength="1000" required></textarea>
                <label for="contact">{{E(T("page.refund.claimContact"))}}</label>
                <input id="contact" name="contact" maxlength="200" autocomplete="email">
                <button type="submit">{{E(T("page.refund.claimSend"))}}</button>
                <p id="result" class="hint" role="status"></p>
              </form>
            </section>
            {{LanguageLinks(id, language)}}
            <script>
              document.getElementById('claim').addEventListener('submit', async function (event) {
                event.preventDefault();
                var result = document.getElementById('result');
                var button = event.target.querySelector('button');
                button.disabled = true;
                try {
                  var response = await fetch('/api/refunds/{{id}}/claim', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'Accept-Language': '{{language}}', 'Idempotency-Key': crypto.randomUUID() },
                    body: JSON.stringify({ text: event.target.text.value, contact: event.target.contact.value })
                  });
                  if (response.ok) {
                    event.target.text.value = '';
                    result.textContent = {{Js(T("page.refund.claimSent"))}};
                  } else {
                    var problem = await response.json().catch(function () { return null; });
                    result.textContent = (problem && problem.detail) || {{Js(T("page.refund.claimFailed"))}};
                  }
                } catch (error) {
                  result.textContent = {{Js(T("page.refund.claimFailed"))}};
                }
                button.disabled = false;
              });
            </script>
            """);

        return Html(HttpStatusCode.OK, language, T("page.refund.title"), body.ToString());
    }

    /// <summary>"Problem barədə bildir" from the page: limited per IP, like the other anonymous claims.</summary>
    [HttpPost("/api/refunds/{id:guid}/claim")]
    [EnableRateLimiting("support-claim")]
    [Idempotent]
    public async Task<ActionResult<SupportClaimReceipt>> Claim(Guid id, [FromBody] RefundClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new OpenRefundClaimCommand(id, request.Text, request.Contact), cancellationToken));
    }

    private string Language(string? requested)
    {
        if (requested is { } asked && Languages.Contains(asked, StringComparer.OrdinalIgnoreCase))
        {
            return asked.ToLowerInvariant();
        }

        var header = Request.Headers.AcceptLanguage.ToString();

        return Languages.FirstOrDefault(language => header.StartsWith(language, StringComparison.OrdinalIgnoreCase)) ?? "az";
    }

    private static string LanguageLinks(Guid id, string current)
        => "<nav class=\"languages\">" + string.Join(" · ", Languages.Select(language => language == current
               ? $"<strong>{language.ToUpperInvariant()}</strong>"
               : $"<a href=\"/refund/{id}?lang={language}\">{language.ToUpperInvariant()}</a>")) + "</nav>";

    private ContentResult Html(HttpStatusCode status, string language, string title, string body)
        => new()
        {
            StatusCode = (int)status,
            ContentType = "text/html; charset=utf-8",
            Content = $$"""
                <!doctype html>
                <html lang="{{language}}">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="robots" content="noindex">
                <title>{{E(title)}} · NeoPrint</title>
                <style>
                {{PublicPageTokens.Css}}
                * { box-sizing: border-box; }
                body { margin: 0; background: var(--neutral-50); color: var(--neutral-900); font: var(--font-body)/1.5 system-ui, "Segoe UI", Roboto, sans-serif; }
                main { max-width: var(--page-width); margin: 0 auto; padding: var(--space-16); }
                .brand { color: var(--brand-600); font-weight: 600; font-size: var(--font-h2); margin: var(--space-8) 0; }
                h1 { font-size: var(--font-display); margin: 0 0 var(--space-16); }
                h2 { font-size: var(--font-h2); margin: 0 0 var(--space-8); }
                .card { background: var(--neutral-0); border: var(--border) solid var(--neutral-200); border-radius: var(--radius-md); padding: var(--space-16); margin-bottom: var(--space-16); }
                .status { border-left: var(--accent-line) solid var(--info-500); }
                .status--success { border-left-color: var(--success-500); }
                .status--danger { border-left-color: var(--danger-500); }
                .status__line { font-weight: 600; margin-top: 0; }
                .status__icon { display: inline-block; min-width: var(--space-24); text-align: center; border-radius: var(--radius-sm); background: var(--info-bg); color: var(--info-500); }
                .status--success .status__icon { background: var(--success-bg); color: var(--success-500); }
                .status--danger .status__icon { background: var(--danger-bg); color: var(--danger-500); }
                .amount { font-size: var(--font-display); font-weight: 600; font-variant-numeric: tabular-nums; margin: var(--space-8) 0; }
                dl { display: grid; grid-template-columns: auto 1fr; gap: var(--space-4) var(--space-12); font-size: var(--font-small); color: var(--neutral-600); }
                dd { margin: 0; color: var(--neutral-800); }
                .hint { color: var(--neutral-600); font-size: var(--font-small); }
                .voucher { font-size: var(--font-h2); font-weight: 600; letter-spacing: var(--space-4); user-select: all; background: var(--neutral-100); border-radius: var(--radius-sm); padding: var(--space-12); text-align: center; }
                label { display: block; font-size: var(--font-small); margin: var(--space-12) 0 var(--space-4); }
                textarea, input { width: 100%; font: inherit; padding: var(--space-12); border: var(--border) solid var(--neutral-200); border-radius: var(--radius-sm); }
                textarea { min-height: calc(var(--touch) * 2); }
                textarea:focus, input:focus, button:focus, a:focus { outline: var(--focus-width) solid var(--focus-ring); outline-offset: var(--focus-width); }
                button { margin-top: var(--space-16); width: 100%; min-height: var(--touch); border: 0; border-radius: var(--radius-sm); background: var(--brand-500); color: var(--neutral-0); font: inherit; font-weight: 600; }
                button:disabled { background: var(--neutral-200); color: var(--neutral-600); }
                .languages { text-align: center; font-size: var(--font-small); color: var(--neutral-600); }
                .languages a { color: var(--brand-600); }
                </style>
                </head>
                <body><main>
                <p class="brand">NeoPrint</p>
                <h1>{{E(title)}}</h1>
                {{body}}
                </main></body>
                </html>
                """,
        };

    private static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>A value as a message key segment: they start with a small letter (<c>Succeeded</c> → <c>succeeded</c>).</summary>
    private static string Key(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    /// <summary>A string for inline script: JSON-encoded, so quotes and tags cannot break out.</summary>
    private static string Js(string text) => System.Text.Json.JsonSerializer.Serialize(text).Replace("</", "<\\/", StringComparison.Ordinal);

    private static string Money(decimal amount, string language)
    {
        var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        format.NumberDecimalSeparator = language == "en" ? "." : ",";
        format.NumberGroupSeparator = language == "en" ? "," : " ";
        return $"{amount.ToString("#,0.00", format)} ₼";
    }

    private static string Date(DateTime utc)
    {
        return Application.Common.BakuTime.From(utc).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    }
}

public sealed record RefundClaimRequest(string? Text, string? Contact);
