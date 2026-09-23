using NeoPrint.Application.Common;

namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Validates one request type.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a validation package: plan section 2.0
/// asks for DataAnnotations plus our own validators. Implementations are
/// registered in DI and picked up by the validation behaviour.
/// </remarks>
public interface IValidator<in TRequest>
{
    /// <summary>Returns the problems with a request; empty means valid.</summary>
    IEnumerable<ValidationError> Validate(TRequest request);
}
