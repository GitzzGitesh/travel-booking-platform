namespace TravelBooking.Modules.Access.Contracts;

/// <summary>
/// The staff permissions (security rules: permission-based, e.g. <c>refunds.approve</c>), published by the Access module
/// (ADR 0002, ADR 0022). Roles are bundles of these, defined in the Access module. A module with an admin endpoint
/// requires one of these through <c>StaffIdentity.PolicyFor</c>; a new endpoint adds its permission here and to the roles
/// that may use it.
/// </summary>
public static class StaffPermissions
{
    /// <summary>Read orders, their items and timelines (operations queues).</summary>
    public const string OrdersRead = "orders.read";

    /// <summary>Settle a booking in manual review by a supplier lookup (never by someone's statement).</summary>
    public const string BookingsReviewResolve = "bookings.review.resolve";

    public static IReadOnlyList<string> All { get; } = [OrdersRead, BookingsReviewResolve];
}
