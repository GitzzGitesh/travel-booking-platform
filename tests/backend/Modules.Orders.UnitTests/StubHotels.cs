using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Hotels.Contracts;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>The Hotels selection contract, scripted: a confirmed selection for its owner, or the reason it is not.</summary>
internal sealed class StubHotelSelections(HotelSelectionUnavailable? unavailable = null, string owner = "cust-1") : IHotelSelections
{
    public Money Price { get; set; } = new(450m, new CurrencyCode("XTS"));

    public HotelSelectionUnavailable? Unavailable { get; set; } = unavailable;

    public List<Guid> Revalidated { get; } = [];

    public Task<Result<BookableHotelSelection, HotelSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(selectedOfferId, customerId));

    public Task<Result<BookableHotelSelection, HotelSelectionUnavailable>> RevalidateAsync(Guid selectedOfferId, string customerId, CancellationToken cancellationToken)
    {
        Revalidated.Add(selectedOfferId);
        return Task.FromResult(Answer(selectedOfferId, customerId));
    }

    private Result<BookableHotelSelection, HotelSelectionUnavailable> Answer(Guid selectedOfferId, string customerId) =>
        customerId != owner
            ? Result<BookableHotelSelection, HotelSelectionUnavailable>.Failure(HotelSelectionUnavailable.NotFound)
            : Unavailable is { } reason
                ? Result<BookableHotelSelection, HotelSelectionUnavailable>.Failure(reason)
                : Result<BookableHotelSelection, HotelSelectionUnavailable>.Success(new BookableHotelSelection(
                    selectedOfferId, Price, OrderTests.Now.AddMinutes(30), null, null, 2, 1, 0, new DateOnly(2027, 4, 10), new DateOnly(2027, 4, 13),
                    new HotelCancellationTerms(true, OrderTests.Now.AddDays(30), new Money(100m, new CurrencyCode("XTS")))));
}

/// <summary>The Hotels booking contract, scripted: every booking and lookup is recorded, and answers as told.</summary>
internal sealed class StubHotelBookings : IHotelBookings
{
    public HotelBookingResult Next { get; set; } = new(HotelBookingStatus.Booked, "mockhotels", "MH123");

    public HotelBookingResult NextLookup { get; set; } = new(HotelBookingStatus.Unknown, "mockhotels");

    public List<HotelBookingRequest> Booked { get; } = [];

    public List<string> LookedUp { get; } = [];

    public Task<HotelBookingResult> BookAsync(HotelBookingRequest request, CancellationToken cancellationToken)
    {
        Booked.Add(request);
        return Task.FromResult(Next);
    }

    public Task<HotelBookingResult> ReconcileAsync(Guid selectedOfferId, string customerId, string clientReference, Money agreedPrice, CancellationToken cancellationToken)
    {
        LookedUp.Add(clientReference);
        return Task.FromResult(NextLookup);
    }
}
