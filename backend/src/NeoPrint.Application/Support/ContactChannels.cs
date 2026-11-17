using System.Text.Json;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Support;

namespace NeoPrint.Application.Support;

/// <summary>The one <see cref="ContactSettings"/> document (T-438).</summary>
public interface IContactSettingsRepository
{
    Task<ContactSettings?> FindAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ContactSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>One channel as the panel edits it; <paramref name="Link"/> is what the app would open.</summary>
public sealed record ContactChannelView(ContactKind Kind, string Value, bool IsActive, string? Link);

/// <summary>One channel as the app shows it: active, with the link to open.</summary>
public sealed record PublicContactChannel(ContactKind Kind, string Value, string Link);

/// <summary>Every kind, in the app's order, set or not (the panel).</summary>
public sealed record GetContactChannelsQuery : IRequest<IReadOnlyList<ContactChannelView>>;

public sealed record SetContactChannelsCommand(IReadOnlyList<ContactChannelView> Channels, Guid AdminUserId, string? Ip)
    : IRequest<IReadOnlyList<ContactChannelView>>;

/// <summary>The active ones (the app's "Əlaqə"); none set is an empty list, never an error.</summary>
public sealed record GetPublicContactQuery : IRequest<IReadOnlyList<PublicContactChannel>>;

public sealed class ContactChannelHandlers :
    IRequestHandler<GetContactChannelsQuery, IReadOnlyList<ContactChannelView>>,
    IRequestHandler<SetContactChannelsCommand, IReadOnlyList<ContactChannelView>>,
    IRequestHandler<GetPublicContactQuery, IReadOnlyList<PublicContactChannel>>
{
    private readonly IContactSettingsRepository _settings;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public ContactChannelHandlers(IContactSettingsRepository settings, IAuditWriter audit, IClock clock)
    {
        _settings = settings;
        _audit = audit;
        _clock = clock;
    }

    public async Task<IReadOnlyList<ContactChannelView>> Handle(GetContactChannelsQuery request, CancellationToken cancellationToken)
        => Views(await _settings.FindAsync(cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<ContactChannelView>> Handle(SetContactChannelsCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _settings.FindAsync(cancellationToken).ConfigureAwait(false)
            ?? new ContactSettings { Id = ContactSettings.DefaultId, CreatedAt = _clock.UtcNow };
        var before = JsonSerializer.Serialize(settings.Channels);

        var wrong = settings.Set(
            (request.Channels ?? []).Select(channel => new ContactChannel { Kind = channel.Kind, Value = channel.Value, IsActive = channel.IsActive }),
            _clock.UtcNow);

        if (wrong.Count > 0)
        {
            throw new ValidationException(
                [.. wrong.Select(problem => new ValidationError($"channels.{problem.Kind}", problem.Message, ("max", ContactSettings.MaxValueLength)))]);
        }

        await _settings.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "settings.contact", "ContactSettings", "default", before, JsonSerializer.Serialize(settings.Channels), request.Ip, Reason: null),
                cancellationToken)
            .ConfigureAwait(false);

        return Views(settings);
    }

    public async Task<IReadOnlyList<PublicContactChannel>> Handle(GetPublicContactQuery request, CancellationToken cancellationToken)
    {
        var settings = await _settings.FindAsync(cancellationToken).ConfigureAwait(false);
        var shown = new List<PublicContactChannel>();

        foreach (var kind in ContactSettings.Kinds)
        {
            var channel = settings?.Channel(kind);

            // A value the validator would refuse (written straight to the database) is not shown.
            if (channel is { IsActive: true } && ContactSettings.Link(kind, channel.Value) is { } link)
            {
                shown.Add(new PublicContactChannel(kind, channel.Value, link));
            }
        }

        return shown;
    }

    private static IReadOnlyList<ContactChannelView> Views(ContactSettings? settings)
        => [.. ContactSettings.Kinds.Select(kind =>
        {
            var channel = settings?.Channel(kind) ?? new ContactChannel { Kind = kind };
            return new ContactChannelView(kind, channel.Value, channel.IsActive, ContactSettings.Link(kind, channel.Value));
        })];
}
