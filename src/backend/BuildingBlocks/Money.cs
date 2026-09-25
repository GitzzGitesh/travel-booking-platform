namespace TravelBooking.BuildingBlocks;

/// <summary>
/// A decimal amount in one currency (ADR 0010). Arithmetic across currencies throws. Rounding to minor units is
/// applied when producing customer- or provider-facing amounts; that is added with the first pricing story.
/// </summary>
public readonly record struct Money(decimal Amount, CurrencyCode Currency)
{
    public static Money operator +(Money left, Money right) =>
        new(left.Amount + right.Amount, SameCurrency(left, right));

    public static Money operator -(Money left, Money right) =>
        new(left.Amount - right.Amount, SameCurrency(left, right));

    public override string ToString() => $"{Amount} {Currency}";

    private static CurrencyCode SameCurrency(Money left, Money right) =>
        left.Currency == right.Currency
            ? left.Currency
            : throw new InvalidOperationException($"Cannot combine {left.Currency} and {right.Currency} without an explicit FX conversion.");
}
