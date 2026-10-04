using NeoPrint.Application.Files;
using NeoPrint.Application.Common;
using NeoPrint.Layout;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// <see cref="ILayoutDocuments"/> on <c>NeoPrint.Layout</c>, the renderer the
/// kiosk runs too (plan section 2.11).
/// </summary>
/// <remarks>
/// Pictures are decoded in-process by SkiaSharp with the pixel count checked
/// from the header first - the same treatment as the image converter; moving
/// both out of the API process is step 7 (ADR-0005).
/// </remarks>
public sealed class LayoutDocuments : ILayoutDocuments
{
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

    public Task RenderAsync(
        string layoutJson, Func<string, byte[]?> images, Stream output, CancellationToken cancellationToken = default)
    {
        var project = Parse(layoutJson);

        cancellationToken.ThrowIfCancellationRequested();

        // CPU-bound and synchronous; the request thread waits either way.
        Wrap(() =>
        {
            LayoutRenderer.Render(project, images, output);
            return 0;
        });

        return Task.CompletedTask;
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
            throw new ValidationException([new ValidationError("layout", exception.Message)]);
        }
    }
}
