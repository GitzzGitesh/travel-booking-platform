namespace TravelBooking.Modules.Flights.Ports;

/// <summary>An IATA airport code: three upper-case letters, such as LHR.</summary>
public readonly record struct AirportCode
{
    public AirportCode(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"'{value}' is not an IATA airport code (three upper-case letters).", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static bool IsValid(string? value) =>
        value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');

    public override string ToString() => Value;
}
