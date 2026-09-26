using System.Text.Json;

namespace TravelBooking.Integrations.Flights.Amadeus.Dtos;

// Amadeus Self-Service wire model (camelCase JSON): the subset the adapter maps, modelled from the public
// "Flight Offers Search" and "Flight Offers Price" documentation. Internal to this adapter (ADR 0004); verify
// against sandbox (test environment) responses before promoting the adapter beyond MappedFromDocumentation.

internal sealed record AmadeusToken(string AccessToken, int ExpiresIn);

internal sealed record AmadeusSearchRequest(
    List<AmadeusOriginDestination> OriginDestinations,
    List<AmadeusTraveler> Travelers,
    List<string> Sources,
    AmadeusSearchCriteria SearchCriteria);

internal sealed record AmadeusOriginDestination(string Id, string OriginLocationCode, string DestinationLocationCode, AmadeusDateRange DepartureDateTimeRange);

internal sealed record AmadeusDateRange(string Date);

internal sealed record AmadeusTraveler(string Id, string TravelerType, string? AssociatedAdultId = null);

internal sealed record AmadeusSearchCriteria(int MaxFlightOffers, AmadeusFlightFilters FlightFilters);

internal sealed record AmadeusFlightFilters(List<AmadeusCabinRestriction> CabinRestrictions);

internal sealed record AmadeusCabinRestriction(string Cabin, string Coverage, List<string> OriginDestinationIds);

internal sealed record AmadeusPricingRequest(AmadeusPricingData Data);

internal sealed record AmadeusPricingData(string Type, List<JsonElement> FlightOffers);

internal sealed record AmadeusOffer(
    string Id,
    string? LastTicketingDate,
    List<AmadeusItinerary> Itineraries,
    AmadeusPrice Price,
    List<string>? ValidatingAirlineCodes,
    List<AmadeusTravelerPricing>? TravelerPricings);

internal sealed record AmadeusItinerary(string? Duration, List<AmadeusSegment> Segments);

internal sealed record AmadeusSegment(
    string Id,
    AmadeusEndpoint Departure,
    AmadeusEndpoint Arrival,
    string CarrierCode,
    string Number,
    AmadeusOperating? Operating,
    string? Duration);

internal sealed record AmadeusEndpoint(string IataCode, DateTime At);

internal sealed record AmadeusOperating(string? CarrierCode);

internal sealed record AmadeusPrice(string Currency, string? Total, string? Base, string? GrandTotal);

internal sealed record AmadeusTravelerPricing(string TravelerId, string TravelerType, AmadeusPrice Price, List<AmadeusFareDetail>? FareDetailsBySegment);

internal sealed record AmadeusFareDetail(string SegmentId, string? Cabin, string? FareBasis, string? BrandedFare, AmadeusBags? IncludedCheckedBags);

internal sealed record AmadeusBags(int? Quantity, int? Weight, string? WeightUnit);

internal sealed record AmadeusErrors(List<AmadeusError>? Errors);

internal sealed record AmadeusError(int? Status, int? Code, string? Title);
