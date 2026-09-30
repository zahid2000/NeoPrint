namespace NeoPrint.Api.PublicPages;

/// <summary>
/// The design tokens of the public pages (docs/DESIGN_SYSTEM.md §5.6): the
/// light values of the shared scale as CSS custom properties. The page
/// template uses only these; a light value changed there changes here too.
/// </summary>
internal static class PublicPageTokens
{
    public const string Css = """
        :root {
          --neutral-0: #FFFFFF;
          --neutral-50: #F7F8FA;
          --neutral-100: #EDEFF3;
          --neutral-200: #DFE3E9;
          --neutral-600: #5B6473;
          --neutral-800: #2A303A;
          --neutral-900: #171B21;
          --brand-500: #2B5FD9;
          --brand-600: #1F4CBF;
          --brand-700: #173A95;
          --success-500: #16A34A;
          --success-bg: #E8F7EE;
          --warning-500: #D97706;
          --warning-bg: #FEF3E2;
          --danger-500: #DC2626;
          --danger-bg: #FDECEC;
          --info-500: #0284C7;
          --info-bg: #E6F4FB;
          --focus-ring: #2B5FD9;
          --space-4: 4px;
          --space-8: 8px;
          --space-12: 12px;
          --space-16: 16px;
          --space-24: 24px;
          --space-32: 32px;
          --radius-md: 12px;
          --radius-sm: 8px;
          --font-display: 28px;
          --font-h2: 20px;
          --font-body: 16px;
          --font-small: 14px;
          --font-caption: 12px;
          --page-width: 480px;
          --border: 1px;
          --accent-line: 4px;
          --touch: 48px;
          --focus-width: 2px;
        }
        """;
}
