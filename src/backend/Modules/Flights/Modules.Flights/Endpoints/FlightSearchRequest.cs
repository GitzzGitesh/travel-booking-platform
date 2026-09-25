using System.ComponentModel.DataAnnotations;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Endpoints;

/// <summary>
/// A flight search. Public because the .NET 10 validation source generator skips internal types (ADR 0003).
/// The rules mirror the domain types (<see cref="AirportCode"/>, <see cref="PassengerMix"/>), so an invalid search
/// is a 400 at the boundary, never an exception in the domain.
/// </summary>
public sealed class FlightSearchRequest : IValidatableObject
{
    private const string _airportCodePattern = "^[A-Z]{3}$";

    /// <summary>IATA airport code, for example LHR.</summary>
    [Required]
    [RegularExpression(_airportCodePattern, ErrorMessage = "Must be a three-letter upper-case IATA airport code.")]
    public string? Origin { get; init; }

    /// <summary>IATA airport code, for example JFK.</summary>
    [Required]
    [RegularExpression(_airportCodePattern, ErrorMessage = "Must be a three-letter upper-case IATA airport code.")]
    public string? Destination { get; init; }

    /// <summary>Local date at the origin airport.</summary>
    [Required]
    public DateOnly? DepartureDate { get; init; }

    /// <summary>Local date at the destination airport; omit for a one-way search.</summary>
    public DateOnly? ReturnDate { get; init; }

    [Range(1, PassengerMix.MaxSeatedPassengers)]
    public int Adults { get; init; } = 1;

    [Range(0, PassengerMix.MaxSeatedPassengers - 1)]
    public int Children { get; init; }

    /// <summary>Lap infants: at most one per adult.</summary>
    [Range(0, PassengerMix.MaxSeatedPassengers)]
    public int Infants { get; init; }

    // Intentionally the port enum while the port is not frozen (ADR 0014). Split it into an HTTP enum the first time
    // the port enum has to change, so the API contract does not move with it.
    public CabinClass Cabin { get; init; } = CabinClass.Economy;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The JSON converter rejects numbers, but an undefined value must never reach a supplier mapping.
        if (!Enum.IsDefined(Cabin))
        {
            yield return new ValidationResult("Unknown cabin class.", [nameof(Cabin)]);
        }

        if (Origin is not null && Origin == Destination)
        {
            yield return new ValidationResult("Destination must differ from the origin.", [nameof(Destination)]);
        }

        if (ReturnDate < DepartureDate)
        {
            yield return new ValidationResult("The return date cannot be before the departure date.", [nameof(ReturnDate)]);
        }

        if (Infants > Adults)
        {
            yield return new ValidationResult("Each lap infant must travel with an adult.", [nameof(Infants)]);
        }

        if (Adults + Children > PassengerMix.MaxSeatedPassengers)
        {
            yield return new ValidationResult($"At most {PassengerMix.MaxSeatedPassengers} seated passengers (adults and children) per search.", [nameof(Children)]);
        }
    }

    // Only called after validation passed, so the domain constructors cannot throw.
    internal FlightSearchCriteria ToCriteria() => new(
        new AirportCode(Origin!),
        new AirportCode(Destination!),
        DepartureDate!.Value,
        ReturnDate,
        new PassengerMix(Adults, Children, Infants),
        Cabin);
}
