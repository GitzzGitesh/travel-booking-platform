using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Contracts;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>The Flights booking contract, scripted: every booking and lookup is recorded, and answers as told.</summary>
internal sealed class StubBookings : IFlightBookings
{
    public FlightBookingResult Next { get; set; } = new(FlightBookingStatus.Booked, "mock", "LOC123", FlightTicketingStatus.Issued);

    public FlightBookingResult NextLookup { get; set; } = new(FlightBookingStatus.Unknown, "mock");

    public List<FlightBookingRequest> Booked { get; } = [];

    public List<string> LookedUp { get; } = [];

    public bool LookupThrows { get; set; }

    public Task<FlightBookingResult> BookAsync(FlightBookingRequest request, CancellationToken cancellationToken)
    {
        Booked.Add(request);
        return Task.FromResult(Next);
    }

    public Task<FlightBookingResult> ReconcileAsync(Guid selectedOfferId, string customerId, string clientReference, Money agreedPrice, CancellationToken cancellationToken)
    {
        LookedUp.Add(clientReference);
        return LookupThrows ? throw new HttpRequestException("supplier down") : Task.FromResult(NextLookup);
    }
}
