using Microsoft.Extensions.Logging;
using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Providers;
using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Hotels.Domain;
using TravelBooking.Modules.Hotels.Ports;

namespace TravelBooking.Modules.Hotels.Application;

/// <summary>
/// The Hotels side of <see cref="IHotelSelections"/>: <see cref="GetBookableAsync"/> reads the stored selection only;
/// <see cref="RevalidateAsync"/> asks the supplier first (the same handler as the customer's price check).
/// </summary>
internal sealed class HotelSelections(IHotelSelectionStore store, RevalidateHotelSelectionHandler revalidation, HotelProviders providers, TimeProvider timeProvider)
    : IHotelSelections
{
    public async Task<Result<BookableHotelSelection, HotelSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken) =>
        Bookable(await store.FindForUpdateAsync(selectedOfferId, cancellationToken), customerId);

    public async Task<Result<BookableHotelSelection, HotelSelectionUnavailable>> RevalidateAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken)
    {
        if (await store.FindForUpdateAsync(selectedOfferId, cancellationToken) is not { } selection || !selection.IsOwnedBy(customerId))
        {
            return Failure(HotelSelectionUnavailable.NotFound);
        }

        if (providers.Find(selection.ProviderId) is null)
        {
            return Failure(HotelSelectionUnavailable.SupplierCannotBook);
        }

        var revalidated = await revalidation.HandleAsync(selectedOfferId, customerId, cancellationToken);
        if (revalidated.IsSuccess)
        {
            return Bookable(revalidated.Value, customerId);
        }

        return Failure(revalidated.Error switch
        {
            HotelSelectionFailure.NotFound => HotelSelectionUnavailable.NotFound,
            HotelSelectionFailure.PriceChanged => HotelSelectionUnavailable.NeedsPriceCheck,
            HotelSelectionFailure.OfferExpired => HotelSelectionUnavailable.Expired,
            HotelSelectionFailure.SoldOut => HotelSelectionUnavailable.SoldOut,
            _ => HotelSelectionUnavailable.TryAgain, // Conflict or ProviderFailed: nothing changed
        });
    }

    /// <summary>Guests counted as travellers are, by age at check-out (Customers' rule): under 2 an infant, 2 to 11 a child, otherwise an adult.</summary>
    internal static (int Adults, int Children, int Infants) Guests(HotelSelection selection)
    {
        var ages = selection.ChildAgeList;
        return (selection.Adults + ages.Count(a => a >= 12), ages.Count(a => a is >= 2 and < 12), ages.Count(a => a < 2));
    }

    private Result<BookableHotelSelection, HotelSelectionUnavailable> Bookable(HotelSelection? selection, string customerId)
    {
        HotelSelectionUnavailable? unavailable = selection switch
        {
            null => HotelSelectionUnavailable.NotFound,
            _ when !selection.IsOwnedBy(customerId) => HotelSelectionUnavailable.NotFound,
            { Status: HotelSelectionStatus.SoldOut } => HotelSelectionUnavailable.SoldOut,
            { Status: HotelSelectionStatus.Expired } => HotelSelectionUnavailable.Expired,
            { IsFrozen: true } => HotelSelectionUnavailable.NotFound, // ordered, and its booking sent already
            { Status: not HotelSelectionStatus.Confirmed } => HotelSelectionUnavailable.NeedsPriceCheck,
            _ when selection.OfferExpiresAt <= timeProvider.GetUtcNow() => HotelSelectionUnavailable.Expired,
            _ => null,
        };
        if (unavailable is { } reason)
        {
            return Failure(reason);
        }

        var (adults, children, infants) = Guests(selection!);
        return Result<BookableHotelSelection, HotelSelectionUnavailable>.Success(new BookableHotelSelection(
            selection!.Id,
            selection.AgreedPrice,
            selection.OfferExpiresAt,
            selection.AcceptedPriceQuoteId,
            selection.PriceAcceptedAt,
            adults,
            children,
            infants,
            selection.CheckIn,
            selection.CheckOut,
            new HotelCancellationTerms(selection.Refundable, selection.FreeCancellationUntil, selection.PenaltyAfterDeadline)));
    }

    private static Result<BookableHotelSelection, HotelSelectionUnavailable> Failure(HotelSelectionUnavailable reason) =>
        Result<BookableHotelSelection, HotelSelectionUnavailable>.Failure(reason);
}

/// <summary>
/// <see cref="IHotelBookings"/>: the customer's own confirmed selection, booked with the supplier that offered it, with
/// exactly one write per call (never retried) and its outcome classified. Nothing is sent for a selection that is not
/// the customer's or not confirmed, or for guests that do not match the stay: those are NotBooked before any write. A
/// booking found but not as agreed (another price) is a Mismatch, never Booked.
/// </summary>
internal sealed partial class HotelBookings(IHotelSelectionStore store, HotelProviders providers, ILogger<HotelBookings> logger) : IHotelBookings
{
    private static readonly HashSet<ProviderErrorKind> _definitelyNotBooked =
        [ProviderErrorKind.Rejected, ProviderErrorKind.PriceChanged, ProviderErrorKind.SoldOut, ProviderErrorKind.OfferExpired, ProviderErrorKind.InvalidRequest];

    public async Task<HotelBookingResult> BookAsync(HotelBookingRequest request, CancellationToken cancellationToken)
    {
        if (!HotelBookingDetails.IsValidClientReference(request.ClientReference)
            || await store.FindForUpdateAsync(request.SelectedOfferId, cancellationToken) is not { Status: HotelSelectionStatus.Confirmed } selection
            || !selection.IsOwnedBy(request.CustomerId)
            || !GuestsMatch(selection, request.Guests))
        {
            return new HotelBookingResult(HotelBookingStatus.NotBooked, Detail: "The booking request was not valid; nothing was sent to the supplier");
        }

        if (providers.Find(selection.ProviderId) is not { } provider)
        {
            return new HotelBookingResult(HotelBookingStatus.NotBooked, selection.ProviderId, Detail: "The offer's supplier is not available; nothing was sent to it");
        }

        // Frozen before anything is sent: from now on no price check can change what is being booked. Not saved (a
        // concurrent change): nothing is sent.
        if (!selection.StartBooking() || !await store.TrySaveAsync(cancellationToken))
        {
            return new HotelBookingResult(HotelBookingStatus.NotBooked, selection.ProviderId, Detail: "The selection changed at the same time; nothing was sent to the supplier");
        }

        var details = new HotelBookingDetails(
            request.ClientReference,
            new HotelOfferRef(selection.ProviderId, selection.ProviderOfferToken),
            request.AgreedPrice,
            [.. request.Guests.Select(g => new HotelGuest(g.GivenNames, g.Surname, g.ChildAge))],
            request.Contact.Email,
            request.Contact.Phone);
        Result<HotelBookingConfirmation, ProviderError> booked;
        try
        {
            booked = await provider.BookAsync(details, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A started write that threw may still have booked: unknown, looked up by our reference.
            LogBookingThrew(logger, provider.Id, request.ClientReference, exception.GetType().Name);
            return new HotelBookingResult(HotelBookingStatus.Unknown, provider.Id);
        }

        if (!booked.IsSuccess)
        {
            return _definitelyNotBooked.Contains(booked.Error.Kind)
                ? new HotelBookingResult(HotelBookingStatus.NotBooked, provider.Id, Detail: Describe(booked.Error.Kind))
                : new HotelBookingResult(HotelBookingStatus.Unknown, provider.Id);
        }

        return await Frozen(selection, Verify(provider.Id, booked.Value, request.ClientReference, request.AgreedPrice), cancellationToken);
    }

    public async Task<HotelBookingResult> ReconcileAsync(Guid selectedOfferId, string customerId, string clientReference, Money agreedPrice, CancellationToken cancellationToken)
    {
        if (!HotelBookingDetails.IsValidClientReference(clientReference)
            || await store.FindForUpdateAsync(selectedOfferId, cancellationToken) is not { } selection || !selection.IsOwnedBy(customerId))
        {
            return new HotelBookingResult(HotelBookingStatus.Unknown); // nothing to look up with: stays for a person, never "not booked"
        }

        if (providers.Find(selection.ProviderId) is not { } provider)
        {
            return new HotelBookingResult(HotelBookingStatus.Unknown, selection.ProviderId);
        }

        var lookup = await provider.RetrieveBookingAsync(clientReference, cancellationToken);
        if (!lookup.IsSuccess)
        {
            return new HotelBookingResult(HotelBookingStatus.Unknown, provider.Id);
        }

        return lookup.Value.Booking is { } booking
            ? await Frozen(selection, Verify(provider.Id, booking, clientReference, agreedPrice), cancellationToken)
            : new HotelBookingResult(HotelBookingStatus.NotFound, provider.Id);
    }

    // A booked stay's selection is marked Booked (it was frozen when its booking was sent). Not saving it (a concurrent
    // change) never changes the booking outcome: the selection stays frozen as Booking.
    private async Task<HotelBookingResult> Frozen(HotelSelection selection, HotelBookingResult result, CancellationToken cancellationToken)
    {
        if (result.Status is HotelBookingStatus.Booked && selection.MarkBooked() && !await store.TrySaveAsync(cancellationToken))
        {
            LogNotFrozen(logger, selection.Id);
        }

        return result;
    }

    // Exactly the occupancy the room was priced for: the searched adults, and children of the searched ages (at check-out),
    // an adult first. Anything else would be repriced or refused by the property, so nothing is sent.
    private static bool GuestsMatch(HotelSelection selection, IReadOnlyList<HotelBookingGuest> guests) =>
        guests.Count(g => g.IsAdult) == selection.Adults
        && guests.Where(g => !g.IsAdult).Select(g => g.ChildAge!.Value).Order().SequenceEqual(selection.ChildAgeList.Order())
        && guests is [{ IsAdult: true }, ..]
        && guests.All(g => !string.IsNullOrWhiteSpace(g.GivenNames) && !string.IsNullOrWhiteSpace(g.Surname));

    private HotelBookingResult Verify(string providerId, HotelBookingConfirmation confirmation, string clientReference, Money agreedPrice)
    {
        // Booked, but with no reference to find it by: not a disagreement, an answer we cannot use yet. Looked up again.
        if (string.IsNullOrWhiteSpace(confirmation.ConfirmationNumber))
        {
            return new HotelBookingResult(HotelBookingStatus.Unknown, providerId);
        }

        if (confirmation.ProviderId == providerId && confirmation.ClientReference == clientReference && confirmation.TotalPrice == agreedPrice)
        {
            return new HotelBookingResult(HotelBookingStatus.Booked, providerId, confirmation.ConfirmationNumber);
        }

        LogMismatch(logger, providerId, clientReference);
        return new HotelBookingResult(HotelBookingStatus.Mismatch, providerId, confirmation.ConfirmationNumber);
    }

    private static string Describe(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.PriceChanged => "The supplier's price changed; nothing was booked",
        ProviderErrorKind.SoldOut => "The room sold out; nothing was booked",
        ProviderErrorKind.OfferExpired => "The supplier's offer expired; nothing was booked",
        ProviderErrorKind.InvalidRequest => "The supplier refused the booking request; nothing was booked",
        _ => "The supplier refused the booking; nothing was booked",
    };

    // Our ids only: never guest data or supplier messages (security rules).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotel booking call to {ProviderId} for {ClientReference} threw ({ExceptionType}); outcome unknown, it will be looked up")]
    private static partial void LogBookingThrew(ILogger logger, string providerId, string clientReference, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotel selection {SelectionId} is booked but could not be marked Booked (a concurrent change); it stays frozen as Booking")]
    private static partial void LogNotFrozen(ILogger logger, Guid selectionId);

    [LoggerMessage(Level = LogLevel.Error, EventName = "HotelBookingMismatch", Message = "Alert: {ProviderId} holds booking {ClientReference} but not as agreed; it is never charged until a person decides")]
    private static partial void LogMismatch(ILogger logger, string providerId, string clientReference);
}

/// <summary><see cref="IHotelStays"/>: the stored selection's non-personal facts (a read; frozen once booked).</summary>
internal sealed class HotelStays(IHotelSelectionStore store) : IHotelStays
{
    public async Task<HotelStay?> GetStayAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
        await store.FindByIdAsync(selectedOfferId, cancellationToken) is { } s
            ? new HotelStay(
                s.PropertyName, s.AddressLine, s.CityCode, s.CountryCode, s.StarRating, s.TimeZone, s.CheckIn, s.CheckOut, s.Nights,
                s.RoomDescription, s.Board.ToString(), new HotelCancellationTerms(s.Refundable, s.FreeCancellationUntil, s.PenaltyAfterDeadline),
                s.Status is HotelSelectionStatus.Booked)
            : null;
}
