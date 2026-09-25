namespace TravelBooking.BuildingBlocks.UnitTests;

public sealed class MoneyTests
{
    private static readonly CurrencyCode _eur = new("EUR");
    private static readonly CurrencyCode _gbp = new("GBP");

    [Fact]
    public void Same_currency_amounts_add_and_subtract_exactly()
    {
        var sum = new Money(0.1m, _eur) + new Money(0.2m, _eur);

        sum.ShouldBe(new Money(0.3m, _eur));
        (sum - new Money(0.3m, _eur)).ShouldBe(new Money(0m, _eur));
    }

    [Fact]
    public void Different_currencies_never_combine_implicitly() =>
        Should.Throw<InvalidOperationException>(() => new Money(1m, _eur) + new Money(1m, _gbp))
            .Message.ShouldContain("FX conversion");

    [Theory]
    [InlineData("EUR", true)]
    [InlineData("XTS", true)]
    [InlineData("eur", false)]
    [InlineData("EU", false)]
    [InlineData("EURO", false)]
    [InlineData("E1R", false)]
    [InlineData(null, false)]
    public void Currency_codes_must_be_three_upper_case_letters(string? value, bool valid)
    {
        CurrencyCode.IsValid(value).ShouldBe(valid);
        if (!valid && value is not null)
        {
            Should.Throw<ArgumentException>(() => new CurrencyCode(value));
        }
    }
}
