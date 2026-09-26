using System.Globalization;
using System.Xml;
using TravelBooking.BuildingBlocks;
using TravelBooking.Integrations.Flights.Duffel.Dtos;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Duffel;

/// <summary>
/// Duffel ↔ port mapping (the anti-corruption layer, ADR 0004). Duffel's names and codes stop here. Facts Duffel does
/// not state in the mapped fields stay "not stated" rather than guessed.
/// </summary>
internal static class DuffelMapping
{
    public static DuffelOfferRequest ToOfferRequest(FlightSearchCriteria criteria)
    {
        var slices = new List<DuffelSliceRequest> { new(criteria.Origin.Value, criteria.Destination.Value, Date(criteria.DepartureDate)) };
        if (criteria.ReturnDate is { } returnDate)
        {
            slices.Add(new(criteria.Destination.Value, criteria.Origin.Value, Date(returnDate)));
        }

        var passengers = Enumerable.Repeat(new DuffelPassengerRequest("adult"), criteria.Passengers.Adults)
            .Concat(Enumerable.Repeat(new DuffelPassengerRequest("child"), criteria.Passengers.Children))
            .Concat(Enumerable.Repeat(new DuffelPassengerRequest("infant_without_seat"), criteria.Passengers.Infants))
            .ToList();

        return new DuffelOfferRequest(slices, passengers, criteria.Cabin switch
        {
            CabinClass.PremiumEconomy => "premium_economy",
            CabinClass.Business => "business",
            CabinClass.First => "first",
            _ => "economy",
        });
    }

    public static FlightOffer ToOffer(DuffelOffer offer, string providerId)
    {
        var currency = new CurrencyCode(offer.TotalCurrency);
        var slices = offer.Slices.Select(slice => new FlightSlice(slice.Segments.Select(ToSegment).ToList())).ToList();
        return new FlightOffer(new ProviderOfferRef(providerId, offer.Id), new Money(Amount(offer.TotalAmount), currency), offer.ExpiresAt, slices)
        {
            Fare = new FlightFare
            {
                // Duffel states the offer's base and tax totals, not a per-passenger-type split: no breakdown is claimed.
                // The offer owner is the airline selling the offer; confirm it is the ticket-issuing (validating) carrier.
                ValidatingCarrier = offer.Owner?.IataCode,
                Baggage = Baggage(offer),
                Conditions = new FareConditions(Allowance(offer.Conditions?.RefundBeforeDeparture), Allowance(offer.Conditions?.ChangeBeforeDeparture)),
            },
        };
    }

    private static FlightSegment ToSegment(DuffelSegment segment) =>
        new(
            segment.MarketingCarrier.IataCode ?? throw new FormatException("A Duffel segment has no marketing carrier."),
            $"{segment.MarketingCarrier.IataCode}{segment.MarketingCarrierFlightNumber}",
            new AirportCode(segment.Origin.IataCode),
            new AirportCode(segment.Destination.IataCode),
            DateTime.SpecifyKind(segment.DepartingAt, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(segment.ArrivingAt, DateTimeKind.Unspecified))
        {
            OperatingCarrier = segment.OperatingCarrier?.IataCode is { } operating && operating != segment.MarketingCarrier.IataCode ? operating : null,
            Duration = segment.Duration is { } iso ? XmlConvert.ToTimeSpan(iso) : null,
            FareBasis = segment.Passengers?.FirstOrDefault()?.FareBasisCode,
        };

    // Per passenger for the whole itinerary: stated only when every leg includes the same for the first passenger.
    private static BaggageAllowance? Baggage(DuffelOffer offer)
    {
        var legs = offer.Slices.SelectMany(s => s.Segments).Select(s => s.Passengers?.FirstOrDefault()?.Baggages).ToList();
        if (legs.Count == 0 || legs.Any(b => b is null))
        {
            return null;
        }

        var perLeg = legs.Select(b => (Checked: b!.Where(x => x.Type == "checked").Sum(x => x.Quantity), Cabin: b!.Where(x => x.Type == "carry_on").Sum(x => x.Quantity))).Distinct().ToList();
        return perLeg.Count == 1 ? new BaggageAllowance(perLeg[0].Checked, perLeg[0].Cabin) : null;
    }

    private static FareAllowance Allowance(DuffelCondition? condition) => condition switch
    {
        null => FareAllowance.NotStated,
        { Allowed: false } => FareAllowance.NotAllowed,
        { PenaltyAmount: { } penalty } when Amount(penalty) > 0 => FareAllowance.AllowedWithFee,
        { PenaltyAmount: not null } => FareAllowance.Free,
        _ => FareAllowance.NotStated, // allowed, but the penalty is not stated: do not claim it is free
    };

    private static decimal Amount(string value) => decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
