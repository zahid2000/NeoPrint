using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

// What a request has to be before a handler looks at it: the shape of a rule,
// of a warehouse item, of a task. The rules themselves live on the entities
// (EnsureValid); here they are asked before anything is read or written, and a
// request that breaks one is a 400, not a conflict.

/// <inheritdoc cref="SupplyRuleInput" />
public sealed class SupplyRuleValidator : IValidator<CreateSupplyRuleCommand>, IValidator<UpdateSupplyRuleCommand>
{
    public IEnumerable<ValidationError> Validate(CreateSupplyRuleCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Check(request.Rule);
    }

    public IEnumerable<ValidationError> Validate(UpdateSupplyRuleCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Check(request.Rule);
    }

    private static List<ValidationError> Check(SupplyRuleInput? input)
    {
        if (input is null)
        {
            return [new ValidationError("rule", "supplyRules.nameInvalid")];
        }

        try
        {
            SupplyRuleHandlers.Shape(new SupplyAlertRule(), input);

            return [];
        }
        catch (DomainException problem)
        {
            return [new ValidationError("rule", problem.Text)];
        }
    }
}

/// <inheritdoc cref="InventoryItemInput" />
public sealed class InventoryItemValidator : IValidator<CreateInventoryItemCommand>, IValidator<UpdateInventoryItemCommand>
{
    public IEnumerable<ValidationError> Validate(CreateInventoryItemCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Check(request.Item);
    }

    public IEnumerable<ValidationError> Validate(UpdateInventoryItemCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Check(request.Item);
    }

    private static List<ValidationError> Check(InventoryItemInput? input)
    {
        if (input is null)
        {
            return [new ValidationError("item", "inventory.nameInvalid")];
        }

        try
        {
            InventoryHandlers.Shape(new InventoryItem(), input);

            return [];
        }
        catch (DomainException problem)
        {
            return [new ValidationError("item", problem.Text)];
        }
    }
}

/// <inheritdoc cref="RecordInventoryMovementCommand" />
public sealed class InventoryMovementValidator : IValidator<RecordInventoryMovementCommand>
{
    public IEnumerable<ValidationError> Validate(RecordInventoryMovementCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<InventoryMovementType>(request.Type, ignoreCase: false, out var type) || !Enum.IsDefined(type))
        {
            yield return new ValidationError("type", "inventory.movementTypeUnknown");
            yield break;
        }

        if (request.Quantity == 0 || (type != InventoryMovementType.Adjustment && request.Quantity < 0))
        {
            yield return new ValidationError("quantity", "inventory.quantityInvalid");
        }

        if (request.Note is { Length: > ServiceTaskHandlers.MaxNoteLength })
        {
            yield return new ValidationError("note", UserMessage.Of("serviceTasks.noteTooLong", ("max", ServiceTaskHandlers.MaxNoteLength)));
        }
    }
}

/// <inheritdoc cref="CreateServiceTaskCommand" />
public sealed class CreateServiceTaskValidator : IValidator<CreateServiceTaskCommand>
{
    public IEnumerable<ValidationError> Validate(CreateServiceTaskCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<ServiceTaskType>(request.Type, ignoreCase: false, out var type) || !Enum.IsDefined(type))
        {
            yield return new ValidationError("type", "serviceTasks.typeUnknown");
        }

        if (!string.IsNullOrWhiteSpace(request.Priority)
            && (!Enum.TryParse<TaskPriority>(request.Priority, ignoreCase: false, out var priority) || !Enum.IsDefined(priority)))
        {
            yield return new ValidationError("priority", "serviceTasks.priorityUnknown");
        }

        if (request.Notes is { Length: > ServiceTaskHandlers.MaxNoteLength })
        {
            yield return new ValidationError("notes", UserMessage.Of("serviceTasks.noteTooLong", ("max", ServiceTaskHandlers.MaxNoteLength)));
        }
    }
}

/// <inheritdoc cref="UpdateServiceTaskCommand" />
public sealed class UpdateServiceTaskValidator : IValidator<UpdateServiceTaskCommand>
{
    public IEnumerable<ValidationError> Validate(UpdateServiceTaskCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<TaskPriority>(request.Priority, ignoreCase: false, out var priority) || !Enum.IsDefined(priority))
        {
            yield return new ValidationError("priority", "serviceTasks.priorityUnknown");
        }

        if (request.Notes is { Length: > ServiceTaskHandlers.MaxNoteLength })
        {
            yield return new ValidationError("notes", UserMessage.Of("serviceTasks.noteTooLong", ("max", ServiceTaskHandlers.MaxNoteLength)));
        }
    }
}

/// <inheritdoc cref="CancelServiceTaskCommand" />
public sealed class CancelServiceTaskValidator : IValidator<CancelServiceTaskCommand>
{
    public IEnumerable<ValidationError> Validate(CancelServiceTaskCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            yield return new ValidationError("reason", "serviceTasks.reasonRequired");
        }
        else if (request.Reason.Trim().Length > ServiceTaskHandlers.MaxNoteLength)
        {
            yield return new ValidationError("reason", UserMessage.Of("serviceTasks.noteTooLong", ("max", ServiceTaskHandlers.MaxNoteLength)));
        }
    }
}

/// <inheritdoc cref="CompleteServiceTaskCommand" />
public sealed class CompleteServiceTaskValidator : IValidator<CompleteServiceTaskCommand>
{
    public IEnumerable<ValidationError> Validate(CompleteServiceTaskCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Paper is not null && request.Paper.Trim().ToUpperInvariant() is not ("A4" or "A3"))
        {
            yield return new ValidationError("paper", "printJobs.paperInvalid");
        }

        if (request.RefillMode is { } mode && !Enum.IsDefined(mode))
        {
            yield return new ValidationError("refillMode", "kiosks.paperRefillModeInvalid");
        }

        if (request.Quantity is not null && request.InventoryItemId is null)
        {
            yield return new ValidationError("quantity", "serviceTasks.quantityWithoutItem");
        }

        if (request.Quantity is <= 0)
        {
            yield return new ValidationError("quantity", "inventory.quantityInvalid");
        }

        if (request.Note is { Length: > ServiceTaskHandlers.MaxNoteLength })
        {
            yield return new ValidationError("note", UserMessage.Of("serviceTasks.noteTooLong", ("max", ServiceTaskHandlers.MaxNoteLength)));
        }
    }
}
