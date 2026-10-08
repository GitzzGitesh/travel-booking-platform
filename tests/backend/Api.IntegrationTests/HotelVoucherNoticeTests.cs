using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Hotels.Contracts;
using TravelBooking.Modules.Notifications.Application;
using TravelBooking.Modules.Notifications.Domain;
using TravelBooking.Modules.Orders.Contracts;

namespace TravelBooking.Api.IntegrationTests;

/// <summary>
/// The hotel voucher in the booking confirmation (ADR 0030, ADR 0024): the stay's facts from Hotels, the cancellation terms
/// the customer agreed to from the order, the deadline in the hotel's own time zone (labelled), and the confirmation is
/// never lost for a stay that cannot be read.
/// </summary>
public sealed class HotelVoucherNoticeTests
{
    private static readonly Guid _orderId = Guid.NewGuid();
    private static readonly Guid _selection = Guid.NewGuid();
    private static readonly DateTimeOffset _deadline = new(2027, 4, 8, 10, 0, 0, TimeSpan.Zero);

    private static StayNotice Stay(bool refundable = true, string? penalty = "120.00", string zone = "Europe/Paris") => new(
        "Mock Central Hotel", "1 Mock Street", new DateOnly(2027, 4, 10), new DateOnly(2027, 4, 13), 3, "Double room", "Breakfast",
        refundable, refundable ? _deadline : null, penalty, penalty is null ? null : "XTS", zone);

    private static string Text(StayNotice stay, string kind = NoticeTemplates.BookingConfirmed) =>
        NoticeTemplates.Render(kind, "en", new BookingNoticeValues(_orderId, ["MH1234"], "450.00", "XTS", [stay])).TextBody;

    [Fact]
    public void The_voucher_states_the_stay_and_the_deadline_in_the_hotels_time_zone()
    {
        var text = Text(Stay());

        text.ShouldContain("Hotel: Mock Central Hotel, 1 Mock Street");
        text.ShouldContain("Stay: Saturday 10 April 2027 to Tuesday 13 April 2027 (3 nights)");
        text.ShouldContain("Room: Double room, breakfast included");
        text.ShouldContain("Cancellation: Free cancellation if you ask before 8 April 2027, 12:00 (Europe/Paris); after that, 120.00 XTS is kept.");
    }

    [Fact]
    public void No_stated_penalty_non_refundable_and_an_unknown_zone_are_each_said_plainly()
    {
        Text(Stay(penalty: null)).ShouldContain("after that, nothing is refunded.");
        Text(Stay(refundable: false)).ShouldContain("Cancellation: Non-refundable: nothing is refunded if you cancel.");
        Text(Stay(zone: "Not/AZone")).ShouldContain("8 April 2027, 10:00 (UTC)"); // never a mislabelled time
        Text(Stay(), NoticeTemplates.BookingCancelled).ShouldNotContain("Hotel:"); // only confirmations carry the voucher
    }

    [Fact]
    public async Task The_terms_come_from_the_order_and_an_unreadable_stay_never_loses_the_confirmation()
    {
        var store = new Notices();
        var stays = new Stays { Stay = HotelStay(new HotelCancellationTerms(false, null, null)) }; // the selection says non-refundable
        var handler = new OrderBookingSettledHandler(store, stays, new FakeTimeProvider(_deadline), NullLogger<OrderBookingSettledHandler>.Instance);
        var agreed = new BookedItem("Hotel", _selection, "MH1234", CancellationRefundable: true, FreeCancellationUntil: _deadline, CancellationPenalty: 120m, Currency: "XTS");

        await handler.HandleAsync(Settled([agreed]), TestContext.Current.CancellationToken);
        var stay = BookingNoticeValues.FromJson(store.Added.Single().Values).Stays.ShouldNotBeNull().ShouldHaveSingleItem();
        (stay.Refundable, stay.FreeUntil, stay.PenaltyAmount, stay.Hotel).ShouldBe((true, _deadline, "120", "Mock Central Hotel"));

        stays.Throws = true;
        await handler.HandleAsync(Settled([agreed]), TestContext.Current.CancellationToken);
        BookingNoticeValues.FromJson(store.Added[1].Values).Stays.ShouldNotBeNull().ShouldBeEmpty(); // sent without it, with an alert
    }

    [Fact]
    public void An_older_settled_event_without_items_still_reads()
    {
        // As the outbox stored it before Items existed: the same payload, without the property.
        var stored = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Settled([]), JsonSerializerOptions.Web))!.AsObject();
        stored.Remove("items").ShouldBeTrue();
        var json = stored.ToJsonString();

        var settled = JsonSerializer.Deserialize<OrderBookingSettled>(json, JsonSerializerOptions.Web)!;

        (settled.OrderId, settled.Items).ShouldBe((_orderId, null));
    }

    private static OrderBookingSettled Settled(IReadOnlyList<BookedItem> items) =>
        new(Guid.NewGuid(), _deadline, _orderId, BookingOutcome.Confirmed, ["MH1234"], new Money(450m, new CurrencyCode("XTS")), "c1", items);

    private static HotelStay HotelStay(HotelCancellationTerms terms) => new(
        "Mock Central Hotel", "1 Mock Street", "PAR", "ZZ", 4m, "UTC", new DateOnly(2027, 4, 10), new DateOnly(2027, 4, 13), 3, "Double room",
        "Breakfast", terms, Booked: true);

    private sealed class Stays : IHotelStays
    {
        public HotelStay? Stay { get; set; }

        public bool Throws { get; set; }

        public Task<HotelStay?> GetStayAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Throws ? throw new TimeoutException("database") : Task.FromResult(Stay);
    }

    private sealed class Notices : INotificationStore
    {
        public List<Notification> Added { get; } = [];

        public Task<bool> TryAddAsync(Notification notification, CancellationToken cancellationToken)
        {
            Added.Add(notification);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<Guid>> FindDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Notification?> FindAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
