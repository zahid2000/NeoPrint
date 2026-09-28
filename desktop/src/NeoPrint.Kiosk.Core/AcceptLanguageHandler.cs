namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The language the kiosk screen is showing right now (plan section 7.1:
/// az, en, ru, switched from the header).
/// </summary>
/// <remarks>
/// One per process - there is one screen - and read by
/// <see cref="AcceptLanguageHandler"/> on every request, so what the backend
/// says back reads in the language of the screen it lands on.
/// </remarks>
public static class KioskLanguage
{
    private static volatile string _current = "az";

    public static string Current
    {
        get => _current;
        set => _current = string.IsNullOrWhiteSpace(value) ? "az" : value;
    }
}

/// <summary>
/// Sends the screen's language as <c>Accept-Language</c>, which is what the
/// backend writes its messages in (docs/TASKS.md T-043).
/// </summary>
public sealed class AcceptLanguageHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.AcceptLanguage.Clear();
        request.Headers.TryAddWithoutValidation("Accept-Language", KioskLanguage.Current);

        return base.SendAsync(request, cancellationToken);
    }
}
