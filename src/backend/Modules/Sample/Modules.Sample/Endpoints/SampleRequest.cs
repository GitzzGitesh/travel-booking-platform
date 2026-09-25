using System.ComponentModel.DataAnnotations;

namespace TravelBooking.Modules.Sample.Endpoints;

/// <summary>
/// Request used to prove built-in validation: attribute rules plus a cross-field rule.
/// Public because the .NET 10 validation source generator skips internal types.
/// </summary>
public sealed class SampleRequest : IValidatableObject
{
    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string? Name { get; init; }

    [Range(1, 9)]
    public int Quantity { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is { } from && To is { } to && to < from)
        {
            yield return new ValidationResult("To must be on or after From.", [nameof(To)]);
        }
    }
}
