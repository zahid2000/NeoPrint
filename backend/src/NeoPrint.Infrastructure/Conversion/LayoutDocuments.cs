using NeoPrint.Application.Files;
using NeoPrint.Application.Common;
using NeoPrint.Converter;
using NeoPrint.Layout;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// <see cref="ILayoutDocuments"/>: the layout's JSON is read here, with
/// <c>NeoPrint.Layout</c>; drawing it - which decodes the user's pictures - is
/// done by the converter tool, with the renderer the kiosk runs too
/// (plan section 2.11, docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
public sealed class LayoutDocuments : ILayoutDocuments
{
    private const string LayoutFile = "input.json";
    private const string Output = "output.pdf";

    private readonly ConverterTools _tools;

    public LayoutDocuments(ConverterTools tools) => _tools = tools;

    public LayoutOutline Read(string layoutJson)
    {
        var project = Parse(layoutJson);

        return new LayoutOutline(project.Images(), project.Pages.Count, project.Paper.ToString());
    }

    public string RenameImages(string layoutJson, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var project = Parse(layoutJson);

        var renamed = project with
        {
            Pages = [.. project.Pages.Select(page => new LayoutPage(
                [.. page.Elements.Select(element => element with
                {
                    Image = names.TryGetValue(element.Image, out var name) ? name : element.Image,
                })]))],
        };

        return Wrap(() => LayoutJson.Serialize(renamed));
    }

    public ScanDocumentType? FindDocumentType(string key)
        => DocumentPresets.Find(key) is { } preset
            ? new ScanDocumentType(preset.Key, preset.WidthMm, preset.HeightMm, preset.Sensitive)
            : null;

    public async Task RenderAsync(
        string layoutJson, Func<string, byte[]?> images, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(output);

        // Checked here first, so a malformed layout never reaches the tool.
        var project = Parse(layoutJson);

        using var workspace = await _tools.OpenWorkspaceAsync(cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(workspace.PathOf(LayoutFile), layoutJson, cancellationToken).ConfigureAwait(false);

        var pictures = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in project.Images())
        {
            var bytes = images(name) ?? throw Invalid("layout.imageMissing");
            var file = $"picture-{pictures.Count}";

            await File.WriteAllBytesAsync(workspace.PathOf(file), bytes, cancellationToken).ConfigureAwait(false);
            pictures[name] = file;
        }

        var response = await _tools.RunAsync(
                workspace.Directory,
                ToolContract.LayoutRender,
                new ToolRequest(LayoutFile, Output, Pictures: pictures),
                ConverterTools.InteractiveTimeout,
                failFast: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new ValidationException(
                [new ValidationError("layout", ConverterTools.Describe(response, "layout.renderFailed"))]);
        }

        await using var pdf = new FileStream(
            workspace.PathOf(Output), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

        await pdf.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static LayoutProject Parse(string layoutJson) => Wrap(() => LayoutJson.Parse(layoutJson ?? string.Empty));

    /// <summary>A layout problem as a validation error the user can act on.</summary>
    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (LayoutException exception)
        {
            throw Invalid(exception.Message);
        }
    }

    private static ValidationException Invalid(string key) => new([new ValidationError("layout", key)]);
}
