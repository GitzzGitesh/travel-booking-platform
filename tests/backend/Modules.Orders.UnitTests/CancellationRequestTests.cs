using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.UnitTests;

/// <summary>A customer's cancellation request (ADR 0029): open until completed, declined or withdrawn, and only once.</summary>
public sealed class CancellationRequestTests
{
    private static readonly DateTimeOffset _at = new(2027, 2, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_request_opens_for_its_customer_and_order()
    {
        var orderId = Guid.NewGuid();
        var request = CancellationRequest.Open(orderId, "cust-1", "key-1", _at);

        (request.OrderId, request.CustomerId, request.IdempotencyKey, request.Status, request.RequestedAt)
            .ShouldBe((orderId, "cust-1", "key-1", CancellationRequestStatus.Open, _at));
        request.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void Completing_records_the_refund_case_and_who_did_it()
    {
        var request = Open();
        var caseId = Guid.NewGuid();

        request.Complete(caseId, "staff:s1", _at.AddHours(1)).ShouldBeTrue();

        (request.Status, request.ResolvedBy, request.ResolutionNote, request.ResolvedAt)
            .ShouldBe((CancellationRequestStatus.Completed, "staff:s1", $"refund-case:{caseId}", _at.AddHours(1)));
    }

    [Fact]
    public void Declining_keeps_the_staff_reference_and_withdrawing_names_the_customer()
    {
        var declined = Open();
        declined.Decline("staff:s2", "TICKET-7", _at).ShouldBeTrue();
        (declined.Status, declined.ResolutionNote).ShouldBe((CancellationRequestStatus.Declined, "TICKET-7"));

        var withdrawn = Open();
        withdrawn.Withdraw(_at).ShouldBeTrue();
        (withdrawn.Status, withdrawn.ResolvedBy).ShouldBe((CancellationRequestStatus.Withdrawn, "customer:cust-1"));
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Declined")]
    [InlineData("Withdrawn")]
    public void A_handled_request_never_changes_again(string status)
    {
        var handled = Enum.Parse<CancellationRequestStatus>(status);
        var request = At(handled);
        var resolvedAt = request.ResolvedAt;

        request.Complete(Guid.NewGuid(), "staff:s3", _at.AddDays(1)).ShouldBeFalse();
        request.Decline("staff:s3", "TICKET-8", _at.AddDays(1)).ShouldBeFalse();
        request.Withdraw(_at.AddDays(1)).ShouldBeFalse();

        (request.Status, request.ResolvedAt).ShouldBe((handled, resolvedAt));
    }

    private static CancellationRequest Open() => CancellationRequest.Open(Guid.NewGuid(), "cust-1", "key-1", _at);

    private static CancellationRequest At(CancellationRequestStatus status)
    {
        var request = Open();
        _ = status switch
        {
            CancellationRequestStatus.Completed => request.Complete(Guid.NewGuid(), "staff:s1", _at),
            CancellationRequestStatus.Declined => request.Decline("staff:s1", "TICKET-1", _at),
            CancellationRequestStatus.Withdrawn => request.Withdraw(_at),
            _ => true,
        };
        return request;
    }
}
