namespace TravelBooking.Integrations.Flights.Duffel.Dtos;

// Duffel's wire model: the subset the adapter maps, modelled from Duffel's public API documentation (snake_case JSON,
// "data" envelopes). Internal to this adapter (ADR 0004); verify every field against sandbox responses before
// promoting the adapter beyond MappedFromDocumentation.

internal sealed record DuffelEnvelope<T>(T Data);

internal sealed record DuffelErrorEnvelope(List<DuffelError>? Errors);

internal sealed record DuffelError(string? Code, string? Message, string? Type);

internal sealed record DuffelOfferRequest(List<DuffelSliceRequest> Slices, List<DuffelPassengerRequest> Passengers, string CabinClass);

internal sealed record DuffelSliceRequest(string Origin, string Destination, string DepartureDate);

// Duffel accepts a passenger type or an age; we send types (child ages are not collected at search: confirm).
internal sealed record DuffelPassengerRequest(string Type);

internal sealed record DuffelOfferRequestResult(string Id, List<DuffelOffer>? Offers);

internal sealed record DuffelOffer(
    string Id,
    string TotalAmount,
    string TotalCurrency,
    DateTimeOffset ExpiresAt,
    DuffelCarrier? Owner,
    List<DuffelSlice> Slices,
    DuffelConditions? Conditions);

internal sealed record DuffelCarrier(string? IataCode);

internal sealed record DuffelSlice(List<DuffelSegment> Segments);

internal sealed record DuffelSegment(
    DuffelPlace Origin,
    DuffelPlace Destination,
    DateTime DepartingAt,
    DateTime ArrivingAt,
    DuffelCarrier MarketingCarrier,
    string MarketingCarrierFlightNumber,
    DuffelCarrier? OperatingCarrier,
    string? Duration,
    List<DuffelSegmentPassenger>? Passengers);

internal sealed record DuffelPlace(string IataCode);

internal sealed record DuffelSegmentPassenger(string? FareBasisCode, List<DuffelBaggage>? Baggages);

internal sealed record DuffelBaggage(string Type, int Quantity);

internal sealed record DuffelConditions(DuffelCondition? RefundBeforeDeparture, DuffelCondition? ChangeBeforeDeparture);

internal sealed record DuffelCondition(bool Allowed, string? PenaltyAmount, string? PenaltyCurrency);
