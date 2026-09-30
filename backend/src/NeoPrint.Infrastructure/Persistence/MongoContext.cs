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
using NeoPrint.Domain.Idempotency;
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

    /// <summary>
    /// The session of the transaction the current request is running in, if it
    /// is running in one.
    /// </summary>
    /// <remarks>
    /// Held in an <see cref="AsyncLocal{T}"/> rather than passed from handler to
    /// repository: the alternative is a session parameter on every repository
    /// method, which would be forgotten exactly once and then silently write
    /// outside the transaction. <see cref="MongoSet{TDocument}"/> reads it, so
    /// no caller has to know it exists
    /// (<c>docs/adr/ADR-0004-ambient-mongo-session.md</c>).
    ///
    /// Static, because the context may be resolved more than once in a request
    /// while the transaction is one thing.
    /// </remarks>
    public IClientSessionHandle? Session => AmbientSession.Value;

    private static readonly AsyncLocal<IClientSessionHandle?> AmbientSession = new();

    /// <summary>
    /// Makes every collection operation in this asynchronous flow part of
    /// <paramref name="session"/> until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="MongoTransactionRunner"/>. Nesting is refused: two
    /// transactions in one flow would mean the inner one silently joining the
    /// outer, and a handler that thinks it committed when it did not.
    /// </remarks>
    public IDisposable Enlist(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (AmbientSession.Value is not null)
        {
            throw new InvalidOperationException(
                "A transaction is already running in this flow. Nesting them would make the inner " +
                "commit meaningless (docs/adr/ADR-0004-ambient-mongo-session.md).");
        }

        AmbientSession.Value = session;

        return new Enlistment();
    }

    private sealed class Enlistment : IDisposable
    {
        public void Dispose() => AmbientSession.Value = null;
    }

    private MongoSet<TDocument> Set<TDocument>(string name)
        => new(this, _database.GetCollection<TDocument>(name));

    // Collection names are the snake_case names used throughout the plan.
    public MongoSet<User> Users => Set<User>("users");
    public MongoSet<EmailConfirmation> EmailConfirmations => Set<EmailConfirmation>("email_confirmations");
    public MongoSet<RefreshToken> RefreshTokens => Set<RefreshToken>("refresh_tokens");
    public MongoSet<Consent> Consents => Set<Consent>("consents");
    public MongoSet<LegalDocument> LegalDocuments => Set<LegalDocument>("legal_documents");

    public MongoSet<AdminUser> AdminUsers => Set<AdminUser>("admin_users");
    public MongoSet<AdminRole> AdminRoles => Set<AdminRole>("admin_roles");

    public MongoSet<Kiosk> Kiosks => Set<Kiosk>("kiosks");
    public MongoSet<KioskDevice> KioskDevices => Set<KioskDevice>("kiosk_devices");
    public MongoSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>("enrollment_tokens");

    public MongoSet<AdminSetting> AdminSettings => Set<AdminSetting>("admin_settings");
    public MongoSet<PricingProfile> PricingProfiles => Set<PricingProfile>("pricing_profiles");
    public MongoSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>("feature_flags");
    public MongoSet<Domain.Storage.StorageProvider> StorageProviders => Set<Domain.Storage.StorageProvider>("storage_providers");
    public MongoSet<Domain.Files.StoredFile> Files => Set<Domain.Files.StoredFile>("files");
    public MongoSet<Domain.Storage.StorageSyncJob> StorageSyncJobs => Set<Domain.Storage.StorageSyncJob>("storage_sync_jobs");
    public MongoSet<Domain.Storage.StorageSyncFailure> StorageSyncFailures => Set<Domain.Storage.StorageSyncFailure>("storage_sync_failures");
    public MongoSet<KioskCommand> KioskCommands => Set<KioskCommand>("kiosk_commands");

    public MongoSet<Domain.PrintJobs.PrintJob> PrintJobs => Set<Domain.PrintJobs.PrintJob>("print_jobs");
    public MongoSet<Domain.PrintJobs.PrintAttempt> PrintAttempts => Set<Domain.PrintJobs.PrintAttempt>("print_attempts");
    public MongoSet<Domain.Payments.Payment> Payments => Set<Domain.Payments.Payment>("payments");
    public MongoSet<Domain.Wallets.Wallet> Wallets => Set<Domain.Wallets.Wallet>("wallets");
    public MongoSet<Domain.Refunds.Refund> Refunds => Set<Domain.Refunds.Refund>("refunds");
    public MongoSet<Domain.Refunds.RefundVoucher> RefundVouchers => Set<Domain.Refunds.RefundVoucher>("refund_vouchers");
    public MongoSet<Domain.Incidents.Incident> Incidents => Set<Domain.Incidents.Incident>("incidents");
    public MongoSet<Domain.Support.SupportClaim> SupportClaims => Set<Domain.Support.SupportClaim>("support_claims");
    public MongoSet<Domain.Wallets.WalletTransaction> WalletTransactions => Set<Domain.Wallets.WalletTransaction>("wallet_transactions");

    // The mock bank's own books (Development and tests): through the same
    // session as everything else, so an aborted transaction takes its write
    // back and the next attempt repeats it (ADR-0004).
    public MongoSet<Payments.MockBankTransaction> MockBankTransactions => Set<Payments.MockBankTransaction>("mock_bank_transactions");
    public MongoSet<Payments.MockBankBehaviour> MockBankSettings => Set<Payments.MockBankBehaviour>("mock_bank_settings");
    public MongoSet<Payments.MockCardCharge> MockCardCharges => Set<Payments.MockCardCharge>("mock_card_charges");
    public MongoSet<Payments.MockRefund> MockRefunds => Set<Payments.MockRefund>("mock_refunds");
    public MongoSet<SupplyEvent> SupplyEvents => Set<SupplyEvent>("supply_events");

    /// <summary>A time-series collection, created by <see cref="ApplyRetentionAsync"/> (plan section 2.17).</summary>
    public MongoSet<Domain.Analytics.AnalyticsEvent> AnalyticsEvents => Set<Domain.Analytics.AnalyticsEvent>("analytics_events");

    public MongoSet<Domain.Supplies.SupplyAlertRule> SupplyAlertRules => Set<Domain.Supplies.SupplyAlertRule>("supply_alert_rules");

    public MongoSet<AuditLog> AuditLogs => Set<AuditLog>("audit_logs");

    /// <summary>
    /// Every business state change, in its own collection rather than as an
    /// array inside the aggregate (<c>docs/STATE_MACHINES.md</c>).
    /// </summary>
    public MongoSet<StateTransition> StateTransitions => Set<StateTransition>("state_transitions");

    /// <summary>
    /// Requests that carried an <c>Idempotency-Key</c>
    /// (<c>docs/IDEMPOTENCY.md</c>).
    /// </summary>
    public MongoSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>("idempotency_keys");

    /// <summary>Callbacks an external provider has already sent us.</summary>
    public MongoSet<ProviderEvent> ProviderEvents => Set<ProviderEvent>("provider_events");

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

        // Money keeps its currency with it (plan section 2.3).
        BsonSerializer.RegisterSerializer(new MoneySerializer());
        BsonSerializer.RegisterSerializer(
            new NullableSerializer<Money>(new MoneySerializer()));

        // Decimals that are not money - multipliers, percentages - as
        // Decimal128 (plan section 2.1); a double would not keep them exact.
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

        await CreateAsync(IdempotencyKeys,
            // The claim is an insert against this index, which is what makes it
            // atomic: two requests with the same key cannot both be the first.
            // Scoped by actor and operation, so one caller's key can never
            // replay another caller's response (docs/IDEMPOTENCY.md).
            (Builders<IdempotencyKey>.IndexKeys
                    .Ascending(k => k.Key).Ascending(k => k.ActorKey).Ascending(k => k.Operation),
                Unique("ux_idempotency_keys")),
            // Records are kept 24 hours; the TTL index does the clearing up, so
            // no background job has to (plan section 5).
            (Builders<IdempotencyKey>.IndexKeys.Ascending(k => k.ExpiresAt),
                Expiring("ttl_idempotency_keys", TimeSpan.Zero)));

        await CreateAsync(ProviderEvents,
            // Providers resend a webhook until they get a 2xx, and sometimes
            // afterwards. The database refuses the duplicate, so no code has to
            // remember to check (docs/IDEMPOTENCY.md).
            (Builders<ProviderEvent>.IndexKeys
                    .Ascending(e => e.Provider).Ascending(e => e.ProviderEventId),
                Unique("ux_provider_events")),
            (Builders<ProviderEvent>.IndexKeys.Descending(e => e.ProcessedAt),
                Named("ix_provider_events_processed")));

        // A user's file list, newest first, and the migration's reference
        // switch, which finds every file pointing at the source provider.
        await CreateAsync(Files,
            (Builders<Domain.Files.StoredFile>.IndexKeys
                .Ascending(file => file.UserId).Descending(file => file.CreatedAt), Named("ix_files_user_created")),
            (Builders<Domain.Files.StoredFile>.IndexKeys.Ascending("Original.ProviderId"), Named("ix_files_original_provider")));

        await CreateAsync(KioskCommands,
            (Builders<KioskCommand>.IndexKeys
                .Ascending(command => command.KioskId).Ascending(command => command.Status).Descending(command => command.CreatedAt),
                Named("ix_kiosk_commands_kiosk_status_created")));

        await CreateAsync(PrintJobs,
            // The code is what a person types to find a job; one job per code.
            (Builders<Domain.PrintJobs.PrintJob>.IndexKeys.Ascending(job => job.JobCode), Unique("ux_print_jobs_code")),
            // The queue: a kiosk's jobs by status, in the order they joined.
            (Builders<Domain.PrintJobs.PrintJob>.IndexKeys
                .Ascending(job => job.KioskId).Ascending(job => job.Status).Ascending(job => job.QueuedAt),
                Named("ix_print_jobs_kiosk_status_queued")),
            // The admin list, newest first.
            (Builders<Domain.PrintJobs.PrintJob>.IndexKeys.Descending(job => job.CreatedAt), Named("ix_print_jobs_created")),
            // Jobs left waiting for a payment, for the expiry job.
            (Builders<Domain.PrintJobs.PrintJob>.IndexKeys.Ascending(job => job.Status).Ascending(job => job.CreatedAt),
                Named("ix_print_jobs_status_created")));

        await CreateAsync(Payments,
            // One bank transaction is one payment: a callback can never pay a
            // second payment. Sparse, since app payments have no provider.
            (Builders<Domain.Payments.Payment>.IndexKeys.Ascending(p => p.Provider).Ascending(p => p.ProviderRef),
                new CreateIndexOptions { Name = "ux_payments_provider_ref", Unique = true, Sparse = true }),
            (Builders<Domain.Payments.Payment>.IndexKeys.Ascending(p => p.JobId), Named("ix_payments_job")),
            // The expiry and reconciliation jobs.
            (Builders<Domain.Payments.Payment>.IndexKeys.Ascending(p => p.Status).Ascending(p => p.ExpiresAt),
                Named("ix_payments_status_expires")),
            (Builders<Domain.Payments.Payment>.IndexKeys.Descending(p => p.CreatedAt), Named("ix_payments_created")));

        await CreateAsync(Refunds,
            // One refund per decision and part: a redelivered event, a retried
            // consumer or two admins at once make one refund (no double refund).
            (Builders<Domain.Refunds.Refund>.IndexKeys.Ascending(r => r.DedupKey), Unique("ux_refunds_dedup")),
            (Builders<Domain.Refunds.Refund>.IndexKeys.Ascending(r => r.PaymentId), Named("ix_refunds_payment")),
            (Builders<Domain.Refunds.Refund>.IndexKeys.Ascending(r => r.JobId), Named("ix_refunds_job")),
            (Builders<Domain.Refunds.Refund>.IndexKeys.Ascending(r => r.Status).Ascending(r => r.NextAttemptAt),
                Named("ix_refunds_status_next")),
            (Builders<Domain.Refunds.Refund>.IndexKeys.Descending(r => r.CreatedAt), Named("ix_refunds_created")));

        await CreateAsync(RefundVouchers,
            (Builders<Domain.Refunds.RefundVoucher>.IndexKeys.Ascending(v => v.Code), Unique("ux_refund_vouchers_code")),
            (Builders<Domain.Refunds.RefundVoucher>.IndexKeys.Ascending(v => v.RefundId), Unique("ux_refund_vouchers_refund")));

        await CreateAsync(Incidents,
            // One open incident per cause; a resolved one does not stop a new one.
            (Builders<Domain.Incidents.Incident>.IndexKeys.Ascending(i => i.DedupKey),
                new CreateIndexOptions<Domain.Incidents.Incident>
                {
                    Name = "ux_incidents_open_dedup",
                    Unique = true,
                    PartialFilterExpression = Builders<Domain.Incidents.Incident>.Filter.Eq(i => i.Open, true),
                }),
            (Builders<Domain.Incidents.Incident>.IndexKeys.Ascending(i => i.Status).Descending(i => i.CreatedAt),
                Named("ix_incidents_status_created")),
            // What else happened at a kiosk around a suspected job.
            (Builders<Domain.Incidents.Incident>.IndexKeys.Ascending(i => i.KioskId).Descending(i => i.CreatedAt),
                Named("ix_incidents_kiosk_created")));

        await CreateAsync(SupportClaims,
            (Builders<Domain.Support.SupportClaim>.IndexKeys.Ascending(c => c.JobId), Named("ix_support_claims_job")),
            (Builders<Domain.Support.SupportClaim>.IndexKeys.Ascending(c => c.UserId).Descending(c => c.CreatedAt),
                Named("ix_support_claims_user_created")),
            (Builders<Domain.Support.SupportClaim>.IndexKeys.Ascending(c => c.Status).Descending(c => c.CreatedAt),
                Named("ix_support_claims_status_created")));

        await CreateAsync(WalletTransactions,
            // The guard against a double credit or charge: one line per cause
            // (topup:{paymentId}, payment:{paymentId}, refund:{refundId}, ...).
            (Builders<Domain.Wallets.WalletTransaction>.IndexKeys.Ascending(t => t.DedupKey), Unique("ux_wallet_transactions_dedup")),
            // A user's history, newest first.
            (Builders<Domain.Wallets.WalletTransaction>.IndexKeys.Ascending(t => t.UserId).Descending(t => t.CreatedAt),
                Named("ix_wallet_transactions_user_created")));

        // A job or a test page moves a counter once; the tab reads a kiosk's newest first.
        await CreateAsync(SupplyEvents,
            (Builders<SupplyEvent>.IndexKeys.Ascending(movement => movement.DedupKey), Unique("ux_supply_events_dedup")),
            (Builders<SupplyEvent>.IndexKeys.Ascending(movement => movement.KioskId).Descending(movement => movement.At),
                Named("ix_supply_events_kiosk_at")));

        // One row per job and attempt: a report sent again replaces it.
        await CreateAsync(PrintAttempts,
            (Builders<Domain.PrintJobs.PrintAttempt>.IndexKeys
                .Ascending(attempt => attempt.JobId).Ascending(attempt => attempt.AttemptNo),
                Unique("ux_print_attempts_job_attempt")));

        await CreateAsync(StorageSyncJobs,
            (Builders<Domain.Storage.StorageSyncJob>.IndexKeys.Ascending(job => job.Status), Named("ix_storage_sync_jobs_status")));

        // One row per object per job: a retry updates the row rather than
        // adding another, so the failure count is the number of objects.
        await CreateAsync(StorageSyncFailures,
            (Builders<Domain.Storage.StorageSyncFailure>.IndexKeys
                .Ascending(failure => failure.JobId).Ascending(failure => failure.Bucket).Ascending(failure => failure.Key),
                Unique("ux_storage_sync_failures_object")));

        return;

        static CreateIndexOptions Named(string name) => new() { Name = name };

        static CreateIndexOptions Unique(string name) => new() { Name = name, Unique = true };

        static CreateIndexOptions Expiring(string name, TimeSpan after)
            => new() { Name = name, ExpireAfter = after };

        // Index commands are not allowed inside a transaction, and this runs at
        // startup where there is none, so it deliberately goes round the
        // session (see MongoSet<T>.WithoutSession).
        async Task CreateAsync<T>(
            MongoSet<T> collection,
            params (IndexKeysDefinition<T> Keys, CreateIndexOptions Options)[] indexes)
        {
            var models = indexes
                .Select(index => new CreateIndexModel<T>(index.Keys, index.Options))
                .ToList();

            await collection.WithoutSession.Indexes
                .CreateManyAsync(models, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the retention windows an administrator can change
    /// (<c>docs/DATA_RETENTION.md</c>).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EnsureIndexesAsync"/> and run after seeding,
    /// because the durations come from settings that do not exist yet on a
    /// first start.
    ///
    /// A TTL index carries its duration, so changing the setting means changing
    /// the index. That is done with collMod rather than by dropping and
    /// recreating: dropping would leave a window with no index at all, and on a
    /// large collection recreating is not instant.
    /// </remarks>
    public async Task ApplyRetentionAsync(
        TimeSpan stateTransitions, TimeSpan providerEvents, TimeSpan analytics = default, CancellationToken cancellationToken = default)
    {
        await EnsureAnalyticsAsync(analytics, cancellationToken).ConfigureAwait(false);

        await EnsureTtlAsync(
                "state_transitions", nameof(StateTransition.CreatedAt),
                "ttl_state_transitions", stateTransitions, cancellationToken)
            .ConfigureAwait(false);

        await EnsureTtlAsync(
                "provider_events", nameof(ProviderEvent.ProcessedAt),
                "ttl_provider_events", providerEvents, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The analytics events: a time-series collection that expires by itself
    /// after <c>Analytics.RetentionDays</c> (plan section 2.17). Created here,
    /// before the first insert, because an insert into a missing collection
    /// would make an ordinary one.
    /// </summary>
    private async Task EnsureAnalyticsAsync(TimeSpan expireAfter, CancellationToken cancellationToken)
    {
        const string Name = "analytics_events";

        if (expireAfter <= TimeSpan.Zero)
        {
            return;
        }

        var existing = await (await _database
                .ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", Name) }, cancellationToken)
                .ConfigureAwait(false))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing.Count == 0)
        {
            try
            {
                await _database
                    .CreateCollectionAsync(
                        Name,
                        new CreateCollectionOptions
                        {
                            TimeSeriesOptions = new TimeSeriesOptions(
                                nameof(Domain.Analytics.AnalyticsEvent.Timestamp),
                                nameof(Domain.Analytics.AnalyticsEvent.KioskId),
                                TimeSeriesGranularity.Minutes),
                            ExpireAfter = expireAfter,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }
            catch (MongoCommandException exception) when (exception.Code == 48)
            {
                // NamespaceExists: another instance created it a moment ago.
            }
        }

        await _database
            .RunCommandAsync<BsonDocument>(
                new BsonDocument { ["collMod"] = Name, ["expireAfterSeconds"] = (long)expireAfter.TotalSeconds },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Creates a TTL index, or adjusts the duration of the one that is there.</summary>
    private async Task EnsureTtlAsync(
        string collectionName,
        string field,
        string indexName,
        TimeSpan expireAfter,
        CancellationToken cancellationToken)
    {
        // A non-positive window would mean "delete everything immediately",
        // which is never what somebody means by a retention setting.
        if (expireAfter <= TimeSpan.Zero)
        {
            return;
        }

        var seconds = (long)expireAfter.TotalSeconds;
        var collection = _database.GetCollection<BsonDocument>(collectionName);

        var existing = await collection.Indexes
            .List(cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var current = existing.FirstOrDefault(index => index["name"].AsString == indexName);

        if (current is null)
        {
            await collection.Indexes
                .CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending(field),
                        new CreateIndexOptions { Name = indexName, ExpireAfter = expireAfter }),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var currentSeconds = current.TryGetValue("expireAfterSeconds", out var value)
            ? value.ToInt64()
            : -1;

        if (currentSeconds == seconds)
        {
            return;
        }

        await _database
            .RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    ["collMod"] = collectionName,
                    ["index"] = new BsonDocument
                    {
                        ["name"] = indexName,
                        ["expireAfterSeconds"] = seconds,
                    },
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
