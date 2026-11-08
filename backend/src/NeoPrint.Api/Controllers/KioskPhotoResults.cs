using Microsoft.AspNetCore.Mvc;

using NeoPrint.Application.Kiosks;

namespace NeoPrint.Api.Controllers;

/// <summary>How a kiosk's picture goes out (T-340): its own type, never sniffed, cached by version.</summary>
internal static class KioskPhotoResults
{
    public static IActionResult Of(ControllerBase controller, KioskPhotoContent? photo)
    {
        if (photo is null)
        {
            return controller.NotFound();
        }

        var etag = $"\"{photo.Version}\"";
        var headers = controller.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ETag = etag;
        // A new picture is a new version in the URL: the old one may be kept.
        headers.CacheControl = "private, max-age=604800";

        return controller.Request.Headers.IfNoneMatch.ToString() == etag
            ? controller.StatusCode(StatusCodes.Status304NotModified)
            : controller.File(photo.Bytes, photo.ContentType);
    }
}
