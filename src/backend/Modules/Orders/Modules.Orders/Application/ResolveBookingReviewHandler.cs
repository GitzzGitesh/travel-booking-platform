using TravelBooking.BuildingBlocks;
using TravelBooking.BuildingBlocks.Audit;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.Application;

/// <summary>
/// A staff member asks for a booking in manual review to be checked with the supplier. <paramref name="Reason"/> is a
/// ticket reference or a short note (never personal data); <paramref name="StaffId"/> comes from the validated staff token.
/// </summary>
internal sealed record CheckBookingReview(Guid OrderId, Guid ItemId, string StaffId, string Reason, AuditSource Source)
{
    public const int MaxReasonLength = AuditReasons.MaxLength;

    public bool IsValid() => AuditReasons.IsValid(Reason) && StaffId is { Length: > 0 and <= 64 };
}

internal enum BookingReviewFailure
{
    InvalidRequest,
    NotFound,

    /// <summary>The order changed at the same time: nothing was saved; check again.</summary>
    Conflict,
}

/// <param name="Resolved">The check settled the item (Confirmed or Failed); false when it stays in review.</param>
/// <param name="AlreadySettled">The item was no longer in review (a repeat, or settled meanwhile): nothing was done; <paramref name="Status"/> is where it stands.</param>
internal sealed record BookingReviewResult(Guid OrderId, Guid ItemId, FlightOrderItemStatus Status, bool Resolved, bool AlreadySettled = false);

/// <summary>
/// The way out of a booking's manual review (booking-lifecycle.md): never what someone says the supplier holds, only what
/// a lookup by our reference shows (<see cref="FlightBookingOrchestrator.CheckReviewAsync"/>). The item's new state,
/// the payment's settlement (outbox), the timeline entry and the audit entry are saved together. Never books.
/// </summary>
internal sealed class ResolveBookingReviewHandler(IOrderStore store, FlightBookingOrchestrator booking, TimeProvider timeProvider)
{
    public const string Action = "bookings.review.check";

    public async Task<Result<BookingReviewResult, BookingReviewFailure>> HandleAsync(CheckBookingReview command, CancellationToken cancellationToken)
    {
        if (!command.IsValid())
        {
            return Failure(BookingReviewFailure.InvalidRequest);
        }

        if (await store.FindAsync(command.OrderId, cancellationToken) is not { } order
            || order.Items.SingleOrDefault(i => i.Id == command.ItemId) is not { } item)
        {
            return Failure(BookingReviewFailure.NotFound);
        }

        // State-based idempotency (ADR 0022): a repeat after the item left review changes nothing and says where it stands.
        if (item.Status is not FlightOrderItemStatus.ManualReview)
        {
            return Result<BookingReviewResult, BookingReviewFailure>.Success(
                new BookingReviewResult(order.Id, item.Id, item.Status, item.Status is FlightOrderItemStatus.Confirmed or FlightOrderItemStatus.Failed, AlreadySettled: true));
        }

        var context = new TransitionContext(timeProvider.GetUtcNow(), $"staff:{command.StaffId}", command.Source.CorrelationId);
        var after = await booking.CheckReviewAsync(order, item.Id, command.Reason, context, cancellationToken);
        store.Audit(AuditEntry.For(command.Source, context.At, context.Actor, Action, $"order-item:{item.Id}",
            $"{FlightOrderItemStatus.ManualReview}", $"{after}; {command.Reason}"));

        if (await store.TrySaveAsync(cancellationToken))
        {
            return Result<BookingReviewResult, BookingReviewFailure>.Success(new BookingReviewResult(order.Id, item.Id, after, after is not FlightOrderItemStatus.ManualReview));
        }

        // Nothing was saved (the order changed at the same time): the attempt itself is still audited, on its own.
        store.Audit(AuditEntry.For(command.Source, context.At, context.Actor, Action, $"order-item:{item.Id}",
            $"{FlightOrderItemStatus.ManualReview}", $"not saved: the order changed at the same time; {command.Reason}"));
        await store.TrySaveAsync(cancellationToken);
        return Failure(BookingReviewFailure.Conflict);
    }

    private static Result<BookingReviewResult, BookingReviewFailure> Failure(BookingReviewFailure failure) =>
        Result<BookingReviewResult, BookingReviewFailure>.Failure(failure);
}
