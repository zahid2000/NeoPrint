namespace NeoPrint.Infrastructure.Receipts;

/// <summary>
/// The design tokens of the receipt PDF (docs/DESIGN_SYSTEM.md §5.5): the
/// light values of the shared scale - paper is always white. A light colour
/// changed there changes here too.
/// </summary>
internal static class ReceiptTokens
{
    public const string FontFamily = "Inter";

    // Colours (DESIGN_SYSTEM.md §1, light).
    public const string Text = "#171B21";            // --neutral-900
    public const string TextSecondary = "#5B6473";   // --neutral-600
    public const string Divider = "#DFE3E9";         // --neutral-200
    public const string RowBackground = "#F7F8FA";   // --neutral-50
    public const string Accent = "#1F4CBF";          // --brand-600
    public const string Warning = "#D97706";         // --warning-500
    public const string WarningBackground = "#FEF3E2"; // --warning-bg
    public const string QrBackground = "#FFFFFF";    // --neutral-0: a scanner needs contrast

    // Type, in points (§5.5).
    public const float Title = 16;
    public const float Section = 11;
    public const float Body = 10;
    public const float Caption = 8;

    // Spacing, the 4-based scale (§3), in points.
    public const float Space4 = 4;
    public const float Space8 = 8;
    public const float Space12 = 12;
    public const float Space16 = 16;
    public const float Space24 = 24;
    public const float PageMargin = 32;

    /// <summary>The QR's side, and its quiet zone as the image's own white border.</summary>
    public const float QrSize = 96;

    public const float DividerThickness = 1;
}
