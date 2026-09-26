using System.Globalization;
using System.Text.Json;
using System.Xml;
using TravelBooking.BuildingBlocks;
using TravelBooking.Integrations.Flights.Amadeus.Dtos;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Integrations.Flights.Amadeus;

/// <summary>
/// Amadeus ↔ port mapping (the anti-corruption layer, ADR 0004). The offer's own JSON is its opaque reference: Amadeus
/// prices an offer by receiving it back whole (Flight Offers Price), and the core stores the reference verbatim
/// (ADR 0014).
/// </summary>
internal static class AmadeusMapping
{
    public const string Source = "GDS";

    public static AmadeusSearchRequest ToSearchRequest(FlightSearchCriteria criteria, int maxOffers)
    {
        var legs = new List<AmadeusOriginDestination> { new("1", criteria.Origin.Value, criteria.Destination.Value, new(Date(criteria.DepartureDate))) };
        if (criteria.ReturnDate is { } returnDate)
        {
            legs.Add(new("2", criteria.Destination.Value, criteria.Origin.Value, new(Date(returnDate))));
        }

        var travelers = new List<AmadeusTraveler>();
        var passengers = criteria.Passengers;
        for (var i = 0; i < passengers.Adults; i++)
        {
            travelers.Add(new($"{travelers.Count + 1}", "ADULT"));
        }

        for (var i = 0; i < passengers.Children; i++)
        {
            travelers.Add(new($"{travelers.Count + 1}", "CHILD"));
        }

        // An infant on a lap travels with an adult: one infant per adult (the port guarantees infants <= adults).
        for (var i = 0; i < passengers.Infants; i++)
        {
            travelers.Add(new($"{travelers.Count + 1}", "HELD_INFANT", AssociatedAdultId: $"{i + 1}"));
        }

        var cabin = criteria.Cabin switch
        {
            CabinClass.PremiumEconomy => "PREMIUM_ECONOMY",
            CabinClass.Business => "BUSINESS",
            CabinClass.First => "FIRST",
            _ => "ECONOMY",
        };

        return new AmadeusSearchRequest(legs, travelers, [Source],
            new AmadeusSearchCriteria(maxOffers, new AmadeusFlightFilters([new(cabin, "MOST_SEGMENTS", legs.Select(l => l.Id).ToList())])));
    }

    /// <param name="rawOffer">The offer exactly as Amadeus returned it: kept as the reference for pricing.</param>
    /// <param name="expiresAt">
    /// Amadeus states no offer expiry in the mapped fields: the adapter's configured offer lifetime (our policy, to
    /// confirm against the supplier) bounds it, so the core's expiry and revalidation rules still apply.
    /// </param>
    public static FlightOffer ToOffer(JsonElement rawOffer, AmadeusOffer offer, string providerId, DateTimeOffset expiresAt)
    {
        var currency = new CurrencyCode(offer.Price.Currency);
        var total = Amount(offer.Price.GrandTotal ?? offer.Price.Total ?? throw new FormatException("An Amadeus offer has no total."));
        var fareDetails = offer.TravelerPricings?.FirstOrDefault()?.FareDetailsBySegment ?? [];

        var slices = offer.Itineraries.Select(itinerary => new FlightSlice(itinerary.Segments.Select(segment => new FlightSegment(
            segment.CarrierCode,
            $"{segment.CarrierCode}{segment.Number}",
            new AirportCode(segment.Departure.IataCode),
            new AirportCode(segment.Arrival.IataCode),
            DateTime.SpecifyKind(segment.Departure.At, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(segment.Arrival.At, DateTimeKind.Unspecified))
        {
            OperatingCarrier = segment.Operating?.CarrierCode is { } operating && operating != segment.CarrierCode ? operating : null,
            Duration = segment.Duration is { } iso ? XmlConvert.ToTimeSpan(iso) : null,
            FareBasis = fareDetails.FirstOrDefault(d => d.SegmentId == segment.Id)?.FareBasis,
        }).ToList())).ToList();

        return new FlightOffer(new ProviderOfferRef(providerId, rawOffer.GetRawText()), new Money(total, currency), expiresAt, slices)
        {
            Fare = new FlightFare
            {
                PriceBreakdown = Breakdown(offer, currency),
                ValidatingCarrier = offer.ValidatingAirlineCodes?.FirstOrDefault(),
                // Amadeus states included CHECKED bags per segment; our allowance also needs cabin bags, which the mapped
                // fields do not state. Not claimed until the port allows an unstated cabin allowance (follow-up).
                Baggage = null,

                // Fare rules (refund/change) are not in the search response: they come from a separate call. Not stated.
                Conditions = FareConditions.NotStated,

                // lastTicketingDate is a date without a time or zone: read as the start of that day in UTC (the earliest
                // it can mean), so the deadline is never later than the supplier's.
                TicketingDeadline = offer.LastTicketingDate is { } date
                    ? new DateTimeOffset(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    : null,
            },
        };
    }

    // One entry per traveler (Amadeus prices each traveler): base fare, and taxes and fees as total minus base.
    private static FlightPriceBreakdown? Breakdown(AmadeusOffer offer, CurrencyCode currency)
    {
        if (offer.TravelerPricings is not { Count: > 0 } pricings || pricings.Any(p => p.Price.Total is null || p.Price.Base is null))
        {
            return null;
        }

        return new FlightPriceBreakdown([.. pricings.Select(p =>
        {
            var baseFare = Amount(p.Price.Base!);
            return new PassengerFare(TravelerType(p.TravelerType), 1, new Money(baseFare, currency), new Money(Amount(p.Price.Total!) - baseFare, currency));
        })]);
    }

    private static PassengerType TravelerType(string type) => type switch
    {
        "ADULT" or "SENIOR" or "YOUNG" or "STUDENT" => PassengerType.Adult,
        "CHILD" => PassengerType.Child,
        "HELD_INFANT" or "SEATED_INFANT" => PassengerType.Infant,
        _ => throw new FormatException($"Unmapped Amadeus traveler type {type}."),
    };

    private static decimal Amount(string value) => decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
