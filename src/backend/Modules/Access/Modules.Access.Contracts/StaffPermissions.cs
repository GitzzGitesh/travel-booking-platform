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

    /// <summary>Read payment attempts, their history and the attempt-limit review list (Q10).</summary>
    public const string PaymentsRead = "payments.read";

    /// <summary>Settle a payment attempt in manual review by a provider lookup (never by someone's statement).</summary>
    public const string PaymentsReviewResolve = "payments.review.resolve";

    /// <summary>Place a legal hold on an order's personal data, or request its release (Q9, ADR 0026; on instruction from legal).</summary>
    public const string PersonalDataLegalHold = "personal-data.legal-hold";

    /// <summary>Approve or reject another staff member's request to release a legal hold (checker, ADR 0026): never one's own.</summary>
    public const string PersonalDataLegalHoldApprove = "personal-data.legal-hold.approve";

    /// <summary>Record a cancellation done at the supplier, or propose a refund (maker, ADR 0027).</summary>
    public const string RefundsRequest = "refunds.request";

    /// <summary>Approve or reject another staff member's refund (checker, ADR 0027): never one's own.</summary>
    public const string RefundsApprove = "refunds.approve";

    /// <summary>See role change requests and active role grants.</summary>
    public const string AccessGrantsRead = "access.grants.read";

    /// <summary>Request a role grant or revocation (maker).</summary>
    public const string AccessGrantsRequest = "access.grants.request";

    /// <summary>Approve or reject another staff member's request (checker): never one's own, never for one's own access.</summary>
    public const string AccessGrantsApprove = "access.grants.approve";

    public static IReadOnlyList<string> All { get; } =
    [
        OrdersRead, BookingsReviewResolve, PaymentsRead, PaymentsReviewResolve, PersonalDataLegalHold, PersonalDataLegalHoldApprove,
        RefundsRequest, RefundsApprove,
        AccessGrantsRead, AccessGrantsRequest, AccessGrantsApprove,
    ];
}
