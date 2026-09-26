using Microsoft.Extensions.Time.Testing;
using TravelBooking.BuildingBlocks;
using TravelBooking.Modules.Flights.Contracts;
using TravelBooking.Modules.Orders.Application;
using TravelBooking.Modules.Orders.Domain;

namespace TravelBooking.Modules.Orders.UnitTests;

public sealed class CreateFlightOrderHandlerTests
{
    private static readonly Guid _selection = Guid.NewGuid();

    [Fact]
    public async Task A_confirmed_selection_becomes_an_order_awaiting_payment_at_the_flights_price()
    {
        var store = new FakeStore();

        var result = await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        result.Value.Created.ShouldBeTrue();
        var order = result.Value.Order;
        order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        order.IdempotencyKey.ShouldBe("key-1");
        order.Items.ShouldHaveSingleItem().AgreedPrice.ShouldBe(OrderTests.Price);
        order.Items[0].SelectedOfferId.ShouldBe(_selection);
        order.CreatedAt.ShouldBe(OrderTests.Now);
        store.Orders.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Replaying_the_same_key_and_selection_returns_the_original_order()
    {
        var store = new FakeStore();
        var first = await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        var replay = await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        replay.Value.Created.ShouldBeFalse();
        replay.Value.Order.Id.ShouldBe(first.Value.Order.Id);
        store.Orders.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_same_key_for_another_selection_is_rejected()
    {
        var store = new FakeStore();
        await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        var reused = await Handler(store).HandleAsync(Command("key-1") with { SelectedOfferId = Guid.NewGuid() }, TestContext.Current.CancellationToken);

        reused.Error.ShouldBeOfType<CreateFlightOrderFailure.IdempotencyKeyReused>();
    }

    [Fact]
    public async Task Another_key_for_an_already_ordered_selection_points_to_the_existing_order()
    {
        var store = new FakeStore();
        var first = await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        var second = await Handler(store).HandleAsync(Command("key-2"), TestContext.Current.CancellationToken);

        second.Error.ShouldBe(new CreateFlightOrderFailure.SelectionAlreadyOrdered(first.Value.Order.Id));
        store.Orders.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Another_customers_order_for_the_selection_is_never_revealed()
    {
        var store = new FakeStore();
        await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        var someoneElse = await Handler(store).HandleAsync(Command("key-1", customer: "cust-2"), TestContext.Current.CancellationToken);

        someoneElse.Error.ShouldBe(new CreateFlightOrderFailure.SelectionAlreadyOrdered(null));
    }

    [Fact]
    public async Task The_same_key_from_another_customer_is_independent_of_the_first()
    {
        var store = new FakeStore();
        await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        var other = await Handler(store).HandleAsync(Command("key-1", customer: "cust-2") with { SelectedOfferId = Guid.NewGuid() }, TestContext.Current.CancellationToken);

        other.Value.Created.ShouldBeTrue();
        other.Value.Order.CustomerId.ShouldBe("cust-2");
        other.Value.Order.Timeline[0].Actor.ShouldBe("customer:cust-2");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task An_order_needs_a_signed_in_customer(string customer)
    {
        var result = await Handler(new FakeStore()).HandleAsync(Command("key-1", customer), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<CreateFlightOrderFailure.CustomerRequired>();
    }

    [Theory]
    [InlineData(FlightSelectionUnavailable.NotFound)]
    [InlineData(FlightSelectionUnavailable.NeedsPriceCheck)]
    [InlineData(FlightSelectionUnavailable.Expired)]
    [InlineData(FlightSelectionUnavailable.SoldOut)]
    public async Task A_selection_that_cannot_be_ordered_creates_nothing(FlightSelectionUnavailable reason)
    {
        var store = new FakeStore();

        var result = await Handler(store, new StubSelections(reason)).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(new CreateFlightOrderFailure.SelectionUnavailable(reason));
        store.Orders.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("ümlaut")]
    public async Task Invalid_idempotency_keys_are_rejected(string key)
    {
        var result = await Handler(new FakeStore()).HandleAsync(Command(key), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<CreateFlightOrderFailure.InvalidIdempotencyKey>();
    }

    [Fact]
    public async Task Losing_an_insert_race_to_the_same_key_answers_as_a_replay()
    {
        var store = new FakeStore();
        var winner = OrderTests.NewOrder(_selection);
        store.LoseNextInsertTo(winner);

        var result = await Handler(store).HandleAsync(Command("key-1"), TestContext.Current.CancellationToken);

        result.Value.Created.ShouldBeFalse();
        result.Value.Order.ShouldBeSameAs(winner);
    }

    private static CreateFlightOrder Command(string key, string customer = "cust-1") => new(customer, key, _selection, "trace-0");

    private static CreateFlightOrderHandler Handler(FakeStore store, IFlightSelections? selections = null) =>
        new(selections ?? new StubSelections(null), store, new FakeTimeProvider(OrderTests.Now));

    private sealed class StubSelections(FlightSelectionUnavailable? unavailable) : IFlightSelections
    {
        public Task<Result<BookableFlightSelection, FlightSelectionUnavailable>> GetBookableAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Task.FromResult(unavailable is { } reason
                ? Result<BookableFlightSelection, FlightSelectionUnavailable>.Failure(reason)
                : Result<BookableFlightSelection, FlightSelectionUnavailable>.Success(
                    new BookableFlightSelection(selectedOfferId, OrderTests.Price, OrderTests.Now.AddMinutes(30), null, null)));
    }

    private sealed class FakeStore : IOrderStore
    {
        private Order? _raceWinner;

        public List<Order> Orders { get; } = [];

        public void LoseNextInsertTo(Order winner) => _raceWinner = winner;

        public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId));

        public Task<Order?> FindOwnedAsync(Guid orderId, string customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId && o.CustomerId == customerId));

        public Task<Order?> FindByIdempotencyKeyAsync(string customerId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.SingleOrDefault(o => o.CustomerId == customerId && o.IdempotencyKey == idempotencyKey));

        public Task<Guid?> FindOrderIdBySelectedOfferAsync(Guid selectedOfferId, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.Where(o => o.Items.Any(i => i.SelectedOfferId == selectedOfferId)).Select(o => (Guid?)o.Id).SingleOrDefault());

        public Task<bool> TryAddAsync(Order order, CancellationToken cancellationToken)
        {
            if (_raceWinner is { } winner)
            {
                Orders.Add(winner);
                _raceWinner = null;
                return Task.FromResult(false);
            }

            Orders.Add(order);
            return Task.FromResult(true);
        }

        public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
