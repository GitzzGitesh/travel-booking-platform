using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Modules.Flights;
using TravelBooking.Modules.Orders;
using TravelBooking.Modules.Payments;

var builder = Host.CreateApplicationBuilder(args);

// The same modules as the Api (ADR 0007: one codebase, two hosts); only the Worker runs their background jobs.
builder.Services.AddFlightsModule(builder.Configuration);
builder.Services.AddOrdersModule(builder.Configuration);
builder.Services.AddPaymentsModule(builder.Configuration);

if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
{
    // The mocks are the only providers until a real supplier (Q6) and payment provider (ADR 0006) are chosen. They keep
    // their state in memory, per process: this Worker cannot see payments or bookings the Api made with them.
    builder.Services.AddMockFlightProvider(builder.Configuration);
    builder.Services.AddMockPaymentProvider(builder.Configuration);
}

builder.Services.AddOrdersBackgroundJobs();
builder.Services.AddPaymentsBackgroundJobs();
builder.Services.AddBackgroundJobRunner();

var host = builder.Build();
host.Run();
