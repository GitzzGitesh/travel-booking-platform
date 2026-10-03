namespace TravelBooking.Modules.Orders.Contracts;

/// <summary>A staff member's outcome for a booking in manual review (ADR 0025).</summary>
public enum BookingReviewOutcome
{
    /// <summary>The supplier booking was cancelled at the supplier's desk (its reference is the evidence): nothing is charged for it.</summary>
    CancelledAtSupplier,

    /// <summary>The booking seen not as agreed has the customer's travellers and flights at no more than the agreed price: confirmed and charged the agreed price.</summary>
    AcceptAsBooked,
}
