using TravelBooking.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddWorkerServices();

var host = builder.Build();
host.Run();
