using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Audit;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Features;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Users;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Access to the MongoDB collections (plan section 5).
/// </summary>
/// <remarks>
/// The database runs as a replica set because transactions are needed for the
/// MassTransit outbox and for wallet updates (plan section 4).
/// </remarks>
public sealed class MongoContext
{
    private static int _conventionsRegistered;

    private readonly IMongoDatabase _database;

    public MongoContext(IMongoClient client, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        Client = client;
        _database = client.GetDatabase(databaseName);
    }

    public IMongoClient Client { get; }

    public IMongoDatabase Database => _database;

    // Collection names are the snake_case names used throughout the plan.
    public IMongoCollection<User> Users => _database.GetCollection<User>("users");
    public IMongoCollection<EmailConfirmation> EmailConfirmations => _database.GetCollection<EmailConfirmation>("email_confirmations");
    public IMongoCollection<RefreshToken> RefreshTokens => _database.GetCollection<RefreshToken>("refresh_tokens");
    public IMongoCollection<Consent> Consents => _database.GetCollection<Consent>("consents");
    public IMongoCollection<LegalDocument> LegalDocuments => _database.GetCollection<LegalDocument>("legal_documents");

    public IMongoCollection<AdminUser> AdminUsers => _database.GetCollection<AdminUser>("admin_users");
    public IMongoCollection<AdminRole> AdminRoles => _database.GetCollection<AdminRole>("admin_roles");

    public IMongoCollection<Kiosk> Kiosks => _database.GetCollection<Kiosk>("kiosks");
    public IMongoCollection<KioskDevice> KioskDevices => _database.GetCollection<KioskDevice>("kiosk_devices");
    public IMongoCollection<EnrollmentToken> EnrollmentTokens => _database.GetCollection<EnrollmentToken>("enrollment_tokens");

    public IMongoCollection<AdminSetting> AdminSettings => _database.GetCollection<AdminSetting>("admin_settings");
    public IMongoCollection<PricingProfile> PricingProfiles => _database.GetCollection<PricingProfile>("pricing_profiles");
    public IMongoCollection<FeatureFlag> FeatureFlags => _database.GetCollection<FeatureFlag>("feature_flags");
    public IMongoCollection<Domain.Supplies.SupplyAlertRule> SupplyAlertRules
        => _database.GetCollection<Domain.Supplies.SupplyAlertRule>("supply_alert_rules");

    public IMongoCollection<AuditLog> AuditLogs => _database.GetCollection<AuditLog>("audit_logs");

    /// <summary>
    /// Every business state change, in its own collection rather than as an
    /// array inside the aggregate (<c>docs/STATE_MACHINES.md</c>).
    /// </summary>
    public IMongoCollection<StateTransition> StateTransitions
        => _database.GetCollection<StateTransition>("state_transitions");

    /// <summary>
    /// Registers the serialization conventions. Must run before the first
    /// document is mapped, so it is called from the composition root.
    /// </summary>
    public static void RegisterConventions()
    {
        // MongoDB's convention registry is global; registering twice throws.
        if (Interlocked.Exchange(ref _conventionsRegistered, 1) == 1)
        {
            return;
        }

        var pack = new ConventionPack
        {
            // A document written by an older build must still deserialize.
            new IgnoreExtraElementsConvention(true),
            // Null is absence; there is no reason to store it.
            new IgnoreIfNullConvention(true),
            // Enums as strings: readable in the shell and safe to reorder.
            new EnumRepresentationConvention(BsonType.String),
        };

        ConventionRegistry.Register("neoprint", pack, _ => true);

        // Money is decimal with two places (plan section 2.1); Decimal128 keeps
        // it exact, unlike the double a decimal would otherwise become.
        BsonSerializer.RegisterSerializer(new DecimalSerializer(BsonType.Decimal128));
        BsonSerializer.RegisterSerializer(
            new NullableSerializer<decimal>(new DecimalSerializer(BsonType.Decimal128)));

        // The server stores UTC and the UI renders Asia/Baku (plan section 2.1).
        BsonSerializer.RegisterSerializer(
            new DateTimeSerializer(DateTimeKind.Utc, BsonType.DateTime));

        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
    }

    /// <summary>
    /// Creates the indexes. Idempotent, so it runs on every start.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await CreateAsync(Users,
            (Builders<User>.IndexKeys.Ascending(user => user.Email), Unique("ux_users_email")),
            (Builders<User>.IndexKeys.Ascending(user => user.PublicId), Unique("ux_users_public_id")),
            (Builders<User>.IndexKeys.Ascending(user => user.ReferralCode), Unique("ux_users_referral_code")));

        await CreateAsync(EmailConfirmations,
            (Builders<EmailConfirmation>.IndexKeys.Ascending(c => c.UserId), Named("ix_email_confirmations_user")),
            // Confirmation codes clean themselves up an hour after they expire.
            (Builders<EmailConfirmation>.IndexKeys.Ascending(c => c.ExpiresAt),
                Expiring("ttl_email_confirmations", TimeSpan.FromHours(1))));

        await CreateAsync(RefreshTokens,
            (Builders<RefreshToken>.IndexKeys.Ascending(t => t.TokenHash), Unique("ux_refresh_tokens_hash")),
            (Builders<RefreshToken>.IndexKeys.Ascending(t => t.UserId), Named("ix_refresh_tokens_user")),
            (Builders<RefreshToken>.IndexKeys.Ascending(t => t.ExpiresAt),
                Expiring("ttl_refresh_tokens", TimeSpan.FromDays(7))));

        await CreateAsync(Consents,
            (Builders<Consent>.IndexKeys.Ascending(c => c.UserId).Ascending(c => c.DocType),
                Named("ix_consents_user_doctype")),
            // One acceptance per document version. Accepting twice - a retried
            // request, a double tap - must not leave two rows with different
            // timestamps, which would make it impossible to say when the user
            // actually accepted (docs/DATA_RETENTION.md).
            (Builders<Consent>.IndexKeys
                    .Ascending(c => c.UserId).Ascending(c => c.DocType).Ascending(c => c.Version),
                Unique("ux_consents_user_doctype_version")));

        await CreateAsync(LegalDocuments,
            (Builders<LegalDocument>.IndexKeys
                    .Ascending(d => d.DocType).Ascending(d => d.Language).Descending(d => d.PublishedAt),
                Named("ix_legal_documents_lookup")));

        await CreateAsync(AdminUsers,
            (Builders<AdminUser>.IndexKeys.Ascending(a => a.Email), Unique("ux_admin_users_email")));

        await CreateAsync(AdminRoles,
            (Builders<AdminRole>.IndexKeys.Ascending(r => r.Name), Unique("ux_admin_roles_name")));

        await CreateAsync(Kiosks,
            (Builders<Kiosk>.IndexKeys.Ascending(k => k.Code), Unique("ux_kiosks_code")),
            (Builders<Kiosk>.IndexKeys.Ascending(k => k.Status), Named("ix_kiosks_status")),
            (Builders<Kiosk>.IndexKeys.Ascending(k => k.ActiveDeviceId), Named("ix_kiosks_active_device")));

        await CreateAsync(KioskDevices,
            (Builders<KioskDevice>.IndexKeys.Ascending(d => d.PublicKey), Unique("ux_kiosk_devices_public_key")),
            (Builders<KioskDevice>.IndexKeys.Ascending(d => d.Status), Named("ix_kiosk_devices_status")),
            (Builders<KioskDevice>.IndexKeys.Ascending(d => d.PairingCodeHash), Named("ix_kiosk_devices_pairing_code")),
            (Builders<KioskDevice>.IndexKeys.Ascending(d => d.KioskId), Named("ix_kiosk_devices_kiosk")));

        await CreateAsync(EnrollmentTokens,
            (Builders<EnrollmentToken>.IndexKeys.Ascending(t => t.TokenHash), Unique("ux_enrollment_tokens_hash")),
            (Builders<EnrollmentToken>.IndexKeys.Ascending(t => t.KioskId), Named("ix_enrollment_tokens_kiosk")));

        await CreateAsync(AdminSettings,
            (Builders<AdminSetting>.IndexKeys.Ascending(s => s.Key), Unique("ux_admin_settings_key")));

        await CreateAsync(PricingProfiles,
            (Builders<PricingProfile>.IndexKeys.Ascending(p => p.Name), Unique("ux_pricing_profiles_name")),
            (Builders<PricingProfile>.IndexKeys.Ascending(p => p.IsDefault), Named("ix_pricing_profiles_default")));

        await CreateAsync(FeatureFlags,
            (Builders<FeatureFlag>.IndexKeys.Ascending(f => f.Key), Unique("ux_feature_flags_key")));

        await CreateAsync(SupplyAlertRules,
            (Builders<Domain.Supplies.SupplyAlertRule>.IndexKeys.Ascending(r => r.IsActive),
                Named("ix_supply_alert_rules_active")));

        await CreateAsync(AuditLogs,
            (Builders<AuditLog>.IndexKeys.Descending(a => a.CreatedAt), Named("ix_audit_logs_created")),
            (Builders<AuditLog>.IndexKeys.Ascending(a => a.AdminUserId).Descending(a => a.CreatedAt),
                Named("ix_audit_logs_admin")),
            (Builders<AuditLog>.IndexKeys.Ascending(a => a.EntityType).Ascending(a => a.EntityId),
                Named("ix_audit_logs_entity")),
            // Answers "what else happened in the request that did this?", which
            // is how an incident is traced (docs/OBSERVABILITY.md).
            (Builders<AuditLog>.IndexKeys.Ascending(a => a.CorrelationId),
                Named("ix_audit_logs_correlation")));

        await CreateAsync(StateTransitions,
            // The history of one aggregate, newest first.
            (Builders<StateTransition>.IndexKeys
                    .Ascending(t => t.EntityType).Ascending(t => t.EntityId).Descending(t => t.CreatedAt),
                Named("ix_state_transitions_entity")),
            (Builders<StateTransition>.IndexKeys.Ascending(t => t.CorrelationId),
                Named("ix_state_transitions_correlation")),
            (Builders<StateTransition>.IndexKeys.Descending(t => t.CreatedAt),
                Named("ix_state_transitions_created")));

        return;

        static CreateIndexOptions Named(string name) => new() { Name = name };

        static CreateIndexOptions Unique(string name) => new() { Name = name, Unique = true };

        static CreateIndexOptions Expiring(string name, TimeSpan after)
            => new() { Name = name, ExpireAfter = after };

        async Task CreateAsync<T>(
            IMongoCollection<T> collection,
            params (IndexKeysDefinition<T> Keys, CreateIndexOptions Options)[] indexes)
        {
            var models = indexes
                .Select(index => new CreateIndexModel<T>(index.Keys, index.Options))
                .ToList();

            await collection.Indexes.CreateManyAsync(models, cancellationToken).ConfigureAwait(false);
        }
    }
}
