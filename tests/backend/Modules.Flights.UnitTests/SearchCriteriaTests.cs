using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.UnitTests;

public sealed class SearchCriteriaTests
{
    private static readonly AirportCode _lhr = new("LHR");
    private static readonly AirportCode _jfk = new("JFK");
    private static readonly DateOnly _departure = new(2027, 3, 10);

    [Theory]
    [InlineData("LHR", true)]
    [InlineData("lhr", false)]
    [InlineData("LH", false)]
    [InlineData("LHRX", false)]
    [InlineData("L1R", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Airport_codes_must_be_three_upper_case_letters(string? value, bool valid) =>
        AirportCode.IsValid(value).ShouldBe(valid);

    [Theory]
    [InlineData(1, 0, 0, true)]
    [InlineData(2, 3, 2, true)]
    [InlineData(9, 0, 0, true)]
    [InlineData(5, 4, 5, true)]
    [InlineData(0, 1, 0, false)] // at least one adult
    [InlineData(1, 0, 2, false)] // each lap infant needs an adult
    [InlineData(8, 2, 0, false)] // more than nine seated passengers
    [InlineData(1, -1, 0, false)]
    [InlineData(1, 0, -1, false)]
    public void Passenger_mix_follows_passenger_type_rules(int adults, int children, int infants, bool valid)
    {
        PassengerMix.IsValid(adults, children, infants).ShouldBe(valid);
        if (!valid)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => new PassengerMix(adults, children, infants));
        }
    }

    [Fact]
    public void Origin_and_destination_must_differ() =>
        Should.Throw<ArgumentException>(() => new FlightSearchCriteria(_lhr, _lhr, _departure, null, new PassengerMix(1), CabinClass.Economy));

    [Fact]
    public void Return_cannot_be_before_departure() =>
        Should.Throw<ArgumentException>(() => new FlightSearchCriteria(_lhr, _jfk, _departure, _departure.AddDays(-1), new PassengerMix(1), CabinClass.Economy));

    [Fact]
    public void Undefined_cabins_are_rejected() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new FlightSearchCriteria(_lhr, _jfk, _departure, null, new PassengerMix(1), (CabinClass)99));

    [Fact]
    public void Same_day_return_is_a_round_trip() =>
        new FlightSearchCriteria(_lhr, _jfk, _departure, _departure, new PassengerMix(1), CabinClass.Economy).IsRoundTrip.ShouldBeTrue();
}
