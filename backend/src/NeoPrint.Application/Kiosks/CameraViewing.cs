using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Features;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Crypto;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Kiosks;

// ---------------------------------------------------------------------------
// Watching the kiosk camera from the panel (T-483), with camera.view on a
// kiosk the account sees; every look is on the audit record.
//
// Recorded: the minutes on the file server (T-485), and those the panel asks
// the kiosk for - sent sealed, kept a day on the primary storage provider.
// The backend opens them for the panel; nothing in the clear is stored.
//
// Live: while somebody watches, the kiosk sends a small JPEG about twice a
// second; only the latest is kept, in Redis, for seconds. Nobody watching,
// nothing is sent.
// ---------------------------------------------------------------------------

public interface ICameraRequestRepository
{
    Task AddAsync(CameraRequest request, CancellationToken cancellationToken = default);

    Task<CameraRequest?> FindAsync(Guid kioskId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>The kiosk's requests it has not answered, not yet expired; oldest first.</summary>
    Task<IReadOnlyList<CameraRequest>> ListPendingAsync(Guid kioskId, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>The newest request whose span meets this one, if any.</summary>
    Task<CameraRequest?> LatestAsync(Guid kioskId, DateTime from, DateTime to, CancellationToken cancellationToken = default);

    Task UpdateAsync(CameraRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the requests past their lifetime; returns how many.</summary>
    Task<int> DeleteExpiredAsync(DateTime now, CancellationToken cancellationToken = default);
}

/// <summary>The live picture's short-lived state: who watches, and the latest frame.</summary>
public interface ICameraLiveStore
{
    /// <summary>Somebody watches this kiosk for the next <paramref name="lifetime"/>.</summary>
    Task WatchAsync(Guid kioskId, TimeSpan lifetime, CancellationToken cancellationToken = default);

    Task<bool> IsWatchedAsync(Guid kioskId, CancellationToken cancellationToken = default);

    Task StopAsync(Guid kioskId, CancellationToken cancellationToken = default);

    Task PutFrameAsync(Guid kioskId, CameraLiveFrame frame, TimeSpan lifetime, CancellationToken cancellationToken = default);

    Task<CameraLiveFrame?> GetFrameAsync(Guid kioskId, CancellationToken cancellationToken = default);
}

/// <summary>One live JPEG and when the backend got it.</summary>
public sealed record CameraLiveFrame(byte[] Jpeg, DateTime At);

/// <summary>The camera of one kiosk, as the panel sees a time span.</summary>
/// <param name="CameraOn">The kiosk's camera is turned on (<c>kiosk_camera</c>).</param>
/// <param name="Request">The newest request for minutes of this span, if any.</param>
public sealed record CameraFootageView(bool CameraOn, IReadOnlyList<CameraMinuteView> Minutes, CameraRequestView? Request);

/// <param name="Fetched">Asked of the kiosk, kept a day; otherwise from the file server.</param>
public sealed record CameraMinuteView(Guid Id, DateTime Start, DateTime End, long Bytes, bool Fetched, DateTime ExpiresAt);

public sealed record CameraRequestView(Guid Id, DateTime From, DateTime To, string Status, int Delivered, DateTime CreatedAt, DateTime? DeliveredAt);

/// <summary>One minute opened for the panel: an MP4 to play or save.</summary>
public sealed record CameraMinuteVideo(string FileName, long Length, Stream Content);

/// <param name="Live">Somebody still watches: the kiosk keeps sending.</param>
public sealed record CameraLiveAnswer(bool Live);

/// <summary>The recorded minutes of a span (<c>GET admin/kiosks/{id}/camera/footage</c>).</summary>
public sealed record ListCameraFootageQuery(Guid AdminUserId, Guid KioskId, DateTime From, DateTime To) : IRequest<CameraFootageView>;

/// <summary>Asks the kiosk for the minutes of a span it keeps only on its disk.</summary>
public sealed record RequestCameraFootageCommand(Guid AdminUserId, Guid KioskId, DateTime From, DateTime To, string? Ip) : IRequest<CameraRequestView>;

/// <summary>One minute, opened: on the audit record.</summary>
public sealed record OpenCameraMinuteQuery(Guid AdminUserId, Guid KioskId, Guid MinuteId, string? Ip) : IRequest<CameraMinuteVideo>;

/// <summary>Somebody starts watching live; the kiosk is told.</summary>
public sealed record StartCameraLiveCommand(Guid AdminUserId, Guid KioskId, string? Ip) : IRequest<CameraLiveAnswer>;

/// <summary>The latest live frame, null when none came in the last seconds; asking keeps the watch alive.</summary>
public sealed record GetCameraLiveFrameQuery(Guid AdminUserId, Guid KioskId) : IRequest<CameraLiveFrame?>;

public sealed record StopCameraLiveCommand(Guid AdminUserId, Guid KioskId) : IRequest;

/// <summary>What the panel asked of this kiosk and it has not answered (<c>GET kiosks/me/camera/requests</c>).</summary>
public sealed record ListKioskCameraRequestsQuery(Guid DeviceId) : IRequest<IReadOnlyList<CameraRequestView>>;

/// <summary>One sealed minute the panel asked for (<c>POST kiosks/me/camera/requests/{id}/minutes</c>).</summary>
public sealed record UploadRequestedCameraMinuteCommand(Guid DeviceId, Guid RequestId, Stream Body, DateTime Start, int Seconds)
    : IRequest<CameraSegmentView>;

/// <summary>The kiosk sent everything it had of the span.</summary>
public sealed record CompleteKioskCameraRequestCommand(Guid DeviceId, Guid RequestId, int Minutes) : IRequest<CameraRequestView>;

/// <summary>One live frame (<c>POST kiosks/me/camera/live</c>).</summary>
public sealed record PostKioskCameraFrameCommand(Guid DeviceId, Stream Body) : IRequest<CameraLiveAnswer>;

public sealed class CameraViewingHandlers :
    IRequestHandler<ListCameraFootageQuery, CameraFootageView>,
    IRequestHandler<RequestCameraFootageCommand, CameraRequestView>,
    IRequestHandler<OpenCameraMinuteQuery, CameraMinuteVideo>,
    IRequestHandler<StartCameraLiveCommand, CameraLiveAnswer>,
    IRequestHandler<GetCameraLiveFrameQuery, CameraLiveFrame?>,
    IRequestHandler<StopCameraLiveCommand>,
    IRequestHandler<ListKioskCameraRequestsQuery, IReadOnlyList<CameraRequestView>>,
    IRequestHandler<UploadRequestedCameraMinuteCommand, CameraSegmentView>,
    IRequestHandler<CompleteKioskCameraRequestCommand, CameraRequestView>,
    IRequestHandler<PostKioskCameraFrameCommand, CameraLiveAnswer>
{
    /// <summary>A minute asked of the kiosk is kept this long for viewing.</summary>
    public static readonly TimeSpan FetchedLifetime = TimeSpan.FromHours(24);

    /// <summary>The watch ends this long after the panel last asked for a frame: a closed tab stops the kiosk.</summary>
    public static readonly TimeSpan LiveLease = TimeSpan.FromSeconds(20);

    /// <summary>A frame older than this is not shown: the kiosk stopped sending.</summary>
    public static readonly TimeSpan FrameLifetime = TimeSpan.FromSeconds(10);

    /// <summary>A 640-pixel JPEG is tens of kilobytes; anything near this is not one.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    /// <summary>The longest span listed at once.</summary>
    public static readonly TimeSpan MaxListSpan = TimeSpan.FromDays(1);

    private readonly ICameraSegmentRepository _segments;
    private readonly ICameraRequestRepository _requests;
    private readonly ICameraArchiveStore _store;
    private readonly ICameraKeys _keys;
    private readonly ICameraLiveStore _live;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskDeviceRepository _devices;
    private readonly IPermissionResolver _permissions;
    private readonly IFeatureFlagEvaluator _features;
    private readonly IKioskNotifier _notifier;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public CameraViewingHandlers(
        ICameraSegmentRepository segments,
        ICameraRequestRepository requests,
        ICameraArchiveStore store,
        ICameraKeys keys,
        ICameraLiveStore live,
        IKioskRepository kiosks,
        IKioskDeviceRepository devices,
        IPermissionResolver permissions,
        IFeatureFlagEvaluator features,
        IKioskNotifier notifier,
        IAuditWriter audit,
        IClock clock)
    {
        _segments = segments;
        _requests = requests;
        _store = store;
        _keys = keys;
        _live = live;
        _kiosks = kiosks;
        _devices = devices;
        _permissions = permissions;
        _features = features;
        _notifier = notifier;
        _audit = audit;
        _clock = clock;
    }

    /// <inheritdoc cref="ListCameraFootageQuery" />
    public async Task<CameraFootageView> Handle(ListCameraFootageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var (from, to) = Span(request.From, request.To, MaxListSpan);

        var minutes = await _segments.ListAsync(kiosk.Id, from.AddMinutes(-2), to, cancellationToken).ConfigureAwait(false);
        var asked = await _requests.LatestAsync(kiosk.Id, from, to, cancellationToken).ConfigureAwait(false);

        return new CameraFootageView(
            await CameraOnAsync(kiosk.Id, cancellationToken).ConfigureAwait(false),
            [.. minutes.Where(minute => minute.End > from).OrderBy(minute => minute.Start).Select(Minute)],
            asked is null ? null : View(asked));
    }

    /// <inheritdoc cref="RequestCameraFootageCommand" />
    public async Task<CameraRequestView> Handle(RequestCameraFootageCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var (from, to) = Span(request.From, request.To, CameraRequest.MaxSpan);
        var now = _clock.UtcNow;

        // Older than the longest a kiosk keeps a minute, or all in the future: nothing to send.
        if (to < now.AddDays(-(CameraSettings.MaxRetentionDays + 1)) || from > now)
        {
            throw new ValidationException([new ValidationError("from", "camera.footageSpan")]);
        }

        if (!await CameraOnAsync(kiosk.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("camera.off");
        }

        // Asked twice - a double click, a retry: the kiosk is not asked again.
        var latest = await _requests.LatestAsync(kiosk.Id, from, to, cancellationToken).ConfigureAwait(false);
        if (latest is { Status: CameraRequestStatus.Pending } && latest.From == from && latest.To == to && latest.ExpiresAt > now)
        {
            return View(latest);
        }

        var asked = new CameraRequest
        {
            KioskId = kiosk.Id,
            From = from,
            To = to,
            RequestedBy = request.AdminUserId,
            CreatedAt = now,
            ExpiresAt = now + CameraRequest.Lifetime,
        };

        await _requests.AddAsync(asked, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "camera.footage.request", nameof(Kiosk), kiosk.Id.ToString(), null,
                    $"{from:O} - {to:O}", request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        // Offline, it finds the request when it comes back.
        await _notifier.CameraFootageRequestedAsync(kiosk.Id, asked.Id, cancellationToken).ConfigureAwait(false);

        return View(asked);
    }

    /// <inheritdoc cref="OpenCameraMinuteQuery" />
    public async Task<CameraMinuteVideo> Handle(OpenCameraMinuteQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var minute = await _segments.FindByIdAsync(kiosk.Id, request.MinuteId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("cameraMinute");

        using var key = await _keys.OpenPrivateKeyAsync(minute.KeyId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("camera.minuteUnreadable");

        // Opened into a file that disappears when the panel has it: the
        // clear video is never kept.
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-camera-view-{Guid.NewGuid():N}");
        var plain = new FileStream(
            scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            await using (var sealedMinute = await _store.OpenReadAsync(minute.ProviderId, minute.Key, cancellationToken).ConfigureAwait(false))
            {
                await CameraSegmentCipher.DecryptAsync(sealedMinute, plain, _ => key, cancellationToken).ConfigureAwait(false);
            }

            plain.Position = 0;
        }
        catch (Exception exception) when (exception is CryptoFormatException or CryptoAuthenticationException or UnknownKeyException)
        {
            await plain.DisposeAsync().ConfigureAwait(false);
            throw new DomainException("camera.minuteUnreadable");
        }
        catch
        {
            await plain.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "camera.footage.view", nameof(Kiosk), kiosk.Id.ToString(), null,
                    $"{minute.Start:O}", request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        var name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{kiosk.Code}-{minute.Start:yyyyMMdd-HHmmss}Z.mp4");
        return new CameraMinuteVideo(name, plain.Length, plain);
    }

    /// <inheritdoc cref="StartCameraLiveCommand" />
    public async Task<CameraLiveAnswer> Handle(StartCameraLiveCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        if (!await CameraOnAsync(kiosk.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("camera.off");
        }

        await _live.WatchAsync(kiosk.Id, LiveLease, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "camera.live.view", nameof(Kiosk), kiosk.Id.ToString(), null, null, request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);
        await _notifier.CameraLiveRequestedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return new CameraLiveAnswer(true);
    }

    /// <inheritdoc cref="GetCameraLiveFrameQuery" />
    public async Task<CameraLiveFrame?> Handle(GetCameraLiveFrameQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        // Asking is watching: the kiosk keeps sending while the panel asks.
        await _live.WatchAsync(kiosk.Id, LiveLease, cancellationToken).ConfigureAwait(false);

        var frame = await _live.GetFrameAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        return frame is not null && _clock.UtcNow - frame.At <= FrameLifetime ? frame : null;
    }

    /// <inheritdoc cref="StopCameraLiveCommand" />
    public async Task Handle(StopCameraLiveCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        await _live.StopAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ListKioskCameraRequestsQuery" />
    public async Task<IReadOnlyList<CameraRequestView>> Handle(ListKioskCameraRequestsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kioskId = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        return [.. (await _requests.ListPendingAsync(kioskId, _clock.UtcNow, cancellationToken).ConfigureAwait(false)).Select(View)];
    }

    /// <inheritdoc cref="UploadRequestedCameraMinuteCommand" />
    public async Task<CameraSegmentView> Handle(UploadRequestedCameraMinuteCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kioskId = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var asked = await _requests.FindAsync(kioskId, request.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("cameraRequest");

        var now = _clock.UtcNow;
        var start = DateTime.SpecifyKind(request.Start, DateTimeKind.Utc);

        // A kiosk is an untrusted device: only minutes of the span asked for, while it is asked.
        if (asked.ExpiresAt <= now || request.Seconds is < 1 or > CameraArchiveHandlers.MaxSeconds || !asked.Covers(start))
        {
            throw new DomainException("camera.segmentInvalid");
        }

        var existing = await _segments.FindAsync(kioskId, start, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new CameraSegmentView(existing.Id, existing.Start, existing.End, existing.Bytes, existing.ExpiresAt, AlreadyStored: true);
        }

        var providerId = await _store.PrimaryProviderIdAsync(cancellationToken).ConfigureAwait(false);
        var segment = await CameraArchiveHandlers.StoreAsync(
                request.Body, kioskId, start, request.Seconds, providerId, now + FetchedLifetime, fetched: true,
                _keys, _store, now, cancellationToken)
            .ConfigureAwait(false);

        if (!await _segments.TryAddAsync(segment, cancellationToken).ConfigureAwait(false))
        {
            var other = await _segments.FindAsync(kioskId, start, cancellationToken).ConfigureAwait(false);
            segment = other ?? segment;
            return new CameraSegmentView(segment.Id, segment.Start, segment.End, segment.Bytes, segment.ExpiresAt, AlreadyStored: true);
        }

        return new CameraSegmentView(segment.Id, segment.Start, segment.End, segment.Bytes, segment.ExpiresAt, AlreadyStored: false);
    }

    /// <inheritdoc cref="CompleteKioskCameraRequestCommand" />
    public async Task<CameraRequestView> Handle(CompleteKioskCameraRequestCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kioskId = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var asked = await _requests.FindAsync(kioskId, request.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("cameraRequest");

        if (asked.Status == CameraRequestStatus.Pending)
        {
            asked.Deliver(Math.Clamp(request.Minutes, 0, (int)CameraRequest.MaxSpan.TotalMinutes + 2), _clock.UtcNow);
            await _requests.UpdateAsync(asked, cancellationToken).ConfigureAwait(false);
        }

        return View(asked);
    }

    /// <inheritdoc cref="PostKioskCameraFrameCommand" />
    public async Task<CameraLiveAnswer> Handle(PostKioskCameraFrameCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kioskId = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (!await _live.IsWatchedAsync(kioskId, cancellationToken).ConfigureAwait(false))
        {
            // Nobody watches any more: the kiosk stops; the frame is not kept.
            return new CameraLiveAnswer(false);
        }

        var jpeg = await ReadFrameAsync(request.Body, cancellationToken).ConfigureAwait(false);
        await _live.PutFrameAsync(kioskId, new CameraLiveFrame(jpeg, _clock.UtcNow), FrameLifetime, cancellationToken).ConfigureAwait(false);

        return new CameraLiveAnswer(true);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxFrameBytes)
            {
                throw new DomainException("camera.frameInvalid");
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();

        // Only a JPEG reaches the panel's <img>: it starts FF D8 FF.
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
        {
            throw new DomainException("camera.frameInvalid");
        }

        return bytes;
    }

    private static (DateTime From, DateTime To) Span(DateTime from, DateTime to, TimeSpan max)
    {
        var start = DateTime.SpecifyKind(from.ToUniversalTime(), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to.ToUniversalTime(), DateTimeKind.Utc);

        if (end <= start || end - start > max)
        {
            throw new ValidationException([new ValidationError("to", "camera.footageSpan")]);
        }

        return (start, end);
    }

    private Task<bool> CameraOnAsync(Guid kioskId, CancellationToken cancellationToken)
        => _features.IsEnabledAsync(GetKioskConfigHandler.CameraFlag, kioskId, null, cancellationToken);

    private async Task<Kiosk> VisibleKioskAsync(Guid adminUserId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        return kiosk is null || !effective.CanSee(kiosk.Id) ? throw new NotFoundException("kiosk") : kiosk;
    }

    private async Task<Guid> KioskOfAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return device?.KioskId ?? throw new DomainException("devices.notBound");
    }

    private static CameraMinuteView Minute(CameraSegment segment)
        => new(segment.Id, segment.Start, segment.End, segment.Bytes, segment.Fetched, segment.ExpiresAt);

    private static CameraRequestView View(CameraRequest request)
        => new(request.Id, request.From, request.To, request.Status.ToString(), request.Delivered, request.CreatedAt, request.DeliveredAt);
}
