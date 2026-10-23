using MongoDB.Driver;

using NeoPrint.Application.Supplies;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Supplies;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Supplies;

/// <inheritdoc cref="ISupplyAlertRuleRepository" />
public sealed class SupplyAlertRuleRepository : ISupplyAlertRuleRepository
{
    private readonly MongoContext _context;

    public SupplyAlertRuleRepository(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<SupplyAlertRule>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.SupplyAlertRules.Find(FilterDefinition<SupplyAlertRule>.Empty).ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<SupplyAlertRule?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.SupplyAlertRules.Find(rule => rule.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(SupplyAlertRule rule, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.SupplyAlertRules, rule, cancellationToken);

    public Task UpdateAsync(SupplyAlertRule rule, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.SupplyAlertRules, rule, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.SupplyAlertRules.DeleteOneAsync(rule => rule.Id == id, cancellationToken);
}

/// <inheritdoc cref="ISupplyAlertRepository" />
public sealed class SupplyAlertRepository : ISupplyAlertRepository
{
    private readonly MongoContext _context;

    public SupplyAlertRepository(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<SupplyAlert>> ListActiveForKioskAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => await _context.SupplyAlerts
            .Find(alert => alert.KioskId == kioskId && alert.Active)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> TryAddAsync(SupplyAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // Checked first: inside a transaction a duplicate key aborts it, so the
        // unique index is the backstop for a race, not the normal path.
        if (await _context.SupplyAlerts.Find(other => other.Key == alert.Key && other.Active).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await AggregateWriter.InsertAsync(_context, _context.SupplyAlerts, alert, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task UpdateAsync(SupplyAlert alert, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.SupplyAlerts, alert, cancellationToken);

    public async Task<(IReadOnlyList<SupplyAlert> Items, long Total)> ListAsync(
        bool? active,
        Guid? kioskId,
        IReadOnlyCollection<Guid>? scope,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var builder = Builders<SupplyAlert>.Filter;
        var query = builder.Empty;

        if (active is { } wanted)
        {
            query &= builder.Eq(alert => alert.Active, wanted);
        }

        if (kioskId is { } one)
        {
            query &= builder.Eq(alert => alert.KioskId, one);
        }

        if (scope is not null)
        {
            query &= builder.In(alert => alert.KioskId, scope);
        }

        var total = await _context.SupplyAlerts.CountDocumentsAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Critical before Warning ("Critical" sorts first by name as well), then the newest.
        var items = await _context.SupplyAlerts
            .Find(query)
            .SortBy(alert => alert.Level)
            .ThenByDescending(alert => alert.LevelSince)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }
}

/// <inheritdoc cref="IServiceTaskRepository" />
public sealed class ServiceTaskRepository : IServiceTaskRepository
{
    private readonly MongoContext _context;

    public ServiceTaskRepository(MongoContext context) => _context = context;

    public Task<ServiceTask?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.ServiceTasks.Find(task => task.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<ServiceTask?> FindActiveByKeyAsync(string key, CancellationToken cancellationToken = default)
        => _context.ServiceTasks.Find(task => task.Key == key && task.Active).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<bool> TryAddAsync(ServiceTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.Key is { } key
            && await _context.ServiceTasks.Find(other => other.Key == key && other.Active).AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await AggregateWriter.InsertAsync(_context, _context.ServiceTasks, task, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task UpdateAsync(ServiceTask task, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.ServiceTasks, task, cancellationToken);

    public async Task<IReadOnlyList<ServiceTask>> ListActiveAsync(CancellationToken cancellationToken = default)
        => await _context.ServiceTasks.Find(task => task.Active).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ServiceTask>> ListCompletedSinceAsync(DateTime since, CancellationToken cancellationToken = default)
        => await _context.ServiceTasks
            .Find(task => task.Status == ServiceTaskStatus.Done && task.CompletedAt >= since)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<ServiceTask> Items, long Total)> ListAsync(
        IReadOnlyCollection<ServiceTaskStatus>? statuses,
        Guid? kioskId,
        Guid? assignedTo,
        IReadOnlyCollection<Guid>? scope,
        int skip,
        int take,
        CancellationToken cancellationToken = default,
        bool unassignedOnly = false)
    {
        var builder = Builders<ServiceTask>.Filter;
        var query = builder.Empty;

        if (statuses is { Count: > 0 })
        {
            query &= builder.In(task => task.Status, statuses);
        }

        if (kioskId is { } one)
        {
            query &= builder.Eq(task => task.KioskId, one);
        }

        if (assignedTo is { } holder)
        {
            query &= builder.Eq(task => task.AssignedTo, holder);
        }

        if (scope is not null)
        {
            query &= builder.In(task => task.KioskId, scope);
        }

        if (unassignedOnly)
        {
            query &= builder.Eq(task => task.AssignedTo, null);
        }

        var total = await _context.ServiceTasks.CountDocumentsAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);

        // What is still to be done first; within that the most urgent, then the
        // oldest. Priorities are stored by name, so the order is made here - a
        // page of tasks is small, and the active ones are few.
        var all = await _context.ServiceTasks.Find(query).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = all
            .OrderByDescending(task => task.Active)
            .ThenByDescending(task => task.Priority)
            .ThenBy(task => task.Active ? task.CreatedAt : DateTime.MaxValue)
            .ThenByDescending(task => task.CompletedAt)
            .Skip(skip)
            .Take(take)
            .ToList();

        return (items, total);
    }
}

/// <inheritdoc cref="IInventoryRepository" />
public sealed class InventoryRepository : IInventoryRepository
{
    private readonly MongoContext _context;

    public InventoryRepository(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<InventoryItem>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.InventoryItems.Find(FilterDefinition<InventoryItem>.Empty).ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<InventoryItem?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.InventoryItems.Find(item => item.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<InventoryItem?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default)
        => _context.InventoryItems.Find(item => item.Sku == sku).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(InventoryItem item, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.InventoryItems, item, cancellationToken);

    public Task UpdateAsync(InventoryItem item, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.InventoryItems, item, cancellationToken);

    public Task AddMovementAsync(InventoryMovement movement, CancellationToken cancellationToken = default)
        => _context.InventoryMovements.InsertOneAsync(movement, cancellationToken);

    public async Task<(IReadOnlyList<InventoryMovement> Items, long Total)> ListMovementsAsync(
        Guid? itemId, Guid? kioskId, IReadOnlyCollection<Guid>? kioskScope, int skip, int take, CancellationToken cancellationToken = default)
    {
        var builder = Builders<InventoryMovement>.Filter;
        var query = builder.Empty;

        if (itemId is { } item)
        {
            query &= builder.Eq(movement => movement.ItemId, item);
        }

        if (kioskId is { } kiosk)
        {
            query &= builder.Eq(movement => movement.KioskId, kiosk);
        }

        if (kioskScope is not null)
        {
            query &= builder.Eq(movement => movement.KioskId, null)
                     | builder.In(movement => movement.KioskId, kioskScope.Select(id => (Guid?)id));
        }

        var total = await _context.InventoryMovements.CountDocumentsAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = await _context.InventoryMovements
            .Find(query)
            .SortByDescending(movement => movement.At)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }
}

/// <inheritdoc cref="ISupplyUsageReader" />
public sealed class SupplyUsageReader : ISupplyUsageReader
{
    private readonly MongoContext _context;

    public SupplyUsageReader(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<decimal>> DailySheetsAsync(
        Guid kioskId, string? resource, DateTime now, int days, CancellationToken cancellationToken = default)
    {
        // Whole days: the last `days` of them, ending with today so far.
        var firstDay = now.Date.AddDays(-(days - 1));
        var builder = Builders<SupplyEvent>.Filter;
        var query = builder.Eq(movement => movement.KioskId, kioskId)
                    & builder.Gte(movement => movement.At, firstDay)
                    & builder.In(movement => movement.Type, [SupplyEventType.JobPrinted, SupplyEventType.TestPrint]);

        if (resource is not null)
        {
            query &= builder.Eq(movement => movement.Resource, resource);
        }

        var used = await _context.SupplyEvents
            .Find(query)
            .Project(movement => new { movement.At, movement.Delta })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var perDay = new decimal[days];

        foreach (var movement in used)
        {
            var day = (int)(movement.At.Date - firstDay).TotalDays;

            if (day >= 0 && day < days)
            {
                // Printing takes sheets out: the delta is negative.
                perDay[day] += -movement.Delta;
            }
        }

        return perDay;
    }
}
