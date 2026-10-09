using TravelBooking.BuildingBlocks.Background.Persistence;
using TravelBooking.Hosts.Observability;
using TravelBooking.Integrations.Flights.Amadeus;
using TravelBooking.Integrations.Flights.Duffel;
using TravelBooking.Integrations.Flights.Mock;
using TravelBooking.Integrations.Flights.Sabre;
using TravelBooking.Integrations.Flights.Travelport;
using TravelBooking.Integrations.Hotels.Mock;
using TravelBooking.Integrations.Payments.Mock;
using TravelBooking.Integrations.Payments.Stripe;
using TravelBooking.Modules.Customers;
using TravelBooking.Modules.Flights;
using TravelBooking.Modules.Hotels;
using TravelBooking.Modules.Notifications;
using TravelBooking.Modules.Orders;
using TravelBooking.Modules.Payments;

namespace TravelBooking.Worker;

/// <summary>
/// The Worker host's composition (ADR 0007): the modules' services and background jobs, without any web-host concern (no
/// endpoints, so no authentication schemes or authorization policies). In one place so tests build exactly this host.
/// </summary>
public static class WorkerComposition
{
    public static HostApplicationBuilder AddWorkerServices(this HostApplicationBuilder builder)
    {
        builder.AddPlatformTelemetry("travel-booking-worker"); // ADR 0031

        // The same modules as the Api (ADR 0007: one codebase, two hosts); only the Worker runs their background jobs.
        builder.Services.AddFlightsModule(builder.Configuration);
        builder.Services.AddHotelsModule(builder.Configuration); // ADR 0030: hotel bookings are reconciled here
        builder.Services.AddOrdersModule(builder.Configuration);
        builder.Services.AddPaymentsModule(builder.Configuration);
        builder.Services.AddCustomersModule(builder.Configuration);

        // Customer notices (ADR 0024): the Worker only. Without an email provider they wait, safely, until one is configured.
        builder.Services.AddNotificationsModule(builder.Configuration);

        if (builder.Environment.IsDevelopment() || builder.Environment.IsStaging())
        {
            // The flight and hotel mocks keep their state in memory, per process: this Worker cannot see bookings the Api made
            // with them. The payment mock (unless Stripe is enabled, ADR 0006) shares its payments with the Api in SQL (ADR 0032).
            builder.Services.AddMockFlightProvider(builder.Configuration);
            builder.Services.AddMockHotelProvider(builder.Configuration);
            builder.Services.AddRecordingEmailSender(); // until the email provider exists (ADR 0024)
            if (!builder.Configuration.IsStripeEnabled())
            {
                builder.Services.AddMockPaymentProvider(builder.Configuration);
            }
        }

        // Stripe (ADR 0006), as in the Api: the Worker looks payments up and processes its notifications.
        builder.Services.AddStripePaymentProvider(builder.Configuration);

        // Candidate flight suppliers (Q6): each is composed only when enabled in configuration (Integrations:Flights:<Name>),
        // with its credentials from user-secrets / Key Vault. Startup refuses any adapter below ProductionReady outside
        // Development and Staging, and a search provider that does not implement search (Flights:SearchProviderId).
        builder.Services.AddAmadeusFlightProvider(builder.Configuration);
        builder.Services.AddDuffelFlightProvider(builder.Configuration);
        builder.Services.AddSabreFlightProvider(builder.Configuration);
        builder.Services.AddTravelportFlightProvider(builder.Configuration);

        builder.Services.AddOrdersBackgroundJobs();
        builder.Services.AddPaymentsBackgroundJobs();
        builder.Services.AddCustomersBackgroundJobs();
        builder.Services.AddNotificationsBackgroundJobs();
        builder.Services.AddBackgroundJobRunner();
        return builder;
    }
}
