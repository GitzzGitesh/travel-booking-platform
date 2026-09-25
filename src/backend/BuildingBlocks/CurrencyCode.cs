namespace TravelBooking.BuildingBlocks;

/// <summary>An ISO 4217 alphabetic currency code, such as EUR (ADR 0010).</summary>
public readonly record struct CurrencyCode
{
    private readonly string? _value;

    public CurrencyCode(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"'{value}' is not an ISO 4217 alphabetic code (three upper-case letters).", nameof(value));
        }

        _value = value;
    }

    /// <summary>The code. Throws for <c>default(CurrencyCode)</c>, so an unset currency can never pass silently.</summary>
    public string Value => _value ?? throw new InvalidOperationException("The currency code is not set (default value).");

    public static bool IsValid(string? value) =>
        value is { Length: 3 } && value.All(c => c is >= 'A' and <= 'Z');

    public override string ToString() => Value;
}
