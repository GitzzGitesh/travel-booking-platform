using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Flights.Domain;
using TravelBooking.Modules.Flights.Ports;

namespace TravelBooking.Modules.Flights.Application;

/// <summary>
/// <see cref="IFlightBookings"/>: the customer's own confirmed selection, booked with the supplier that offered it
/// (ProviderId routing) through <see cref="FlightSupplierBooking"/>, which makes exactly one write per call and classifies
/// its outcome. Nothing is sent for a selection that is not the customer's, not confirmed, or for passengers that cannot
/// be described to a supplier: those are NotBooked before any write.
/// </summary>
internal sealed class FlightBookings(ISelectedOfferStore store, FlightSupplierBooking supplier) : IFlightBookings
{
    public async Task<FlightBookingResult> BookAsync(FlightBookingRequest request, CancellationToken cancellationToken)
    {
        if (!ClientReference.IsValid(request.ClientReference)
            || await store.FindByIdAsync(request.SelectedOfferId, cancellationToken) is not { Status: SelectedOfferStatus.Confirmed } selection
            || !selection.IsOwnedBy(request.CustomerId)
            || Passengers(request) is not { } passengers)
        {
            return new FlightBookingResult(FlightBookingStatus.NotBooked, Detail: "The booking request was not valid; nothing was sent to the supplier");
        }

        var details = new FlightBookingDetails(
            new ClientReference(request.ClientReference),
            new ProviderOfferRef(selection.ProviderId, selection.ProviderOfferToken),
            request.AgreedPrice,
            passengers,
            new Ports.FlightBookingContact(request.Contact.Email, request.Contact.Phone));
        return Map(await supplier.BookAsync(details, cancellationToken), selection.ProviderId);
    }

    public async Task<FlightBookingResult> ReconcileAsync(Guid selectedOfferId, string customerId, string clientReference, Money agreedPrice, CancellationToken cancellationToken)
    {
        if (!ClientReference.IsValid(clientReference)
            || await store.FindByIdAsync(selectedOfferId, cancellationToken) is not { } selection || !selection.IsOwnedBy(customerId))
        {
            return new FlightBookingResult(FlightBookingStatus.Unknown); // nothing to look up with: stays for a person, never "not booked"
        }

        return Map(await supplier.ReconcileAsync(selection.ProviderId, new ClientReference(clientReference), agreedPrice, cancellationToken), selection.ProviderId);
    }

    private static FlightBookingResult Map(SupplierBookingOutcome outcome, string providerId) => outcome switch
    {
        SupplierBookingOutcome.Booked booked => new FlightBookingResult(
            FlightBookingStatus.Booked, booked.Confirmation.Booking.ProviderId, booked.Confirmation.Booking.Value, Ticketing(booked.Confirmation.Ticketing)),
        SupplierBookingOutcome.NotBooked notBooked => new FlightBookingResult(FlightBookingStatus.NotBooked, providerId, Detail: Describe(notBooked.Reason)),
        SupplierBookingOutcome.NotFound => new FlightBookingResult(FlightBookingStatus.NotFound, providerId),
        SupplierBookingOutcome.Mismatch mismatch => new FlightBookingResult(FlightBookingStatus.Mismatch, providerId, mismatch.Confirmation.Booking.Value),
        _ => new FlightBookingResult(FlightBookingStatus.Unknown, providerId),
    };

    private static FlightTicketingStatus Ticketing(TicketingStatus status) =>
        status is TicketingStatus.Issued ? FlightTicketingStatus.Issued : FlightTicketingStatus.Pending;

    private static string Describe(SupplierBookingFailureReason reason) => reason switch
    {
        SupplierBookingFailureReason.PriceChanged => "The supplier's price changed; nothing was booked",
        SupplierBookingFailureReason.SoldOut => "The flight sold out; nothing was booked",
        SupplierBookingFailureReason.OfferExpired => "The supplier's offer expired; nothing was booked",
        SupplierBookingFailureReason.InvalidRequest => "The supplier refused the booking request; nothing was booked",
        _ => "The supplier refused the booking; nothing was booked",
    };

    // Mapped by name; a name no supplier can take (the port's own check) means nothing is sent.
    private static List<FlightPassenger>? Passengers(FlightBookingRequest request)
    {
        try
        {
            return request.Passengers.Select(p => new FlightPassenger(
                p.Kind switch { FlightPassengerKind.Child => PassengerType.Child, FlightPassengerKind.Infant => PassengerType.Infant, _ => PassengerType.Adult },
                p.GivenNames,
                p.Surname,
                p.DateOfBirth,
                p.Gender is FlightPassengerGender.Male ? PassengerGender.Male : PassengerGender.Female,
                p.Document is { } document
                    ? new FlightPassengerDocument(
                        document.Kind is FlightTravelDocumentKind.IdentityCard ? TravelDocumentType.IdentityCard : TravelDocumentType.Passport,
                        document.Number, document.IssuingCountry, document.Nationality, document.ExpiryDate)
                    : null)).ToList();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
