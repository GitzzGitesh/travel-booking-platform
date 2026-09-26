using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Integrations.Flights.Amadeus;
using TravelBooking.Integrations.Flights.Duffel;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Flights.Sabre;
using TravelBooking.Integrations.Flights.Travelport;
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

// Candidate flight suppliers (Q6): each is composed only when enabled in configuration (Integrations:Flights:<Name>),
// with its credentials from user-secrets / Key Vault. Startup refuses any adapter below ProductionReady outside
// Development and Staging, and a search provider that does not implement search (Flights:SearchProviderId).
builder.Services.AddAmadeusFlightProvider(builder.Configuration);
builder.Services.AddDuffelFlightProvider(builder.Configuration);
builder.Services.AddSabreFlightProvider(builder.Configuration);
builder.Services.AddTravelportFlightProvider(builder.Configuration);

builder.Services.AddOrdersBackgroundJobs();
builder.Services.AddPaymentsBackgroundJobs();
builder.Services.AddBackgroundJobRunner();

var host = builder.Build();
host.Run();
