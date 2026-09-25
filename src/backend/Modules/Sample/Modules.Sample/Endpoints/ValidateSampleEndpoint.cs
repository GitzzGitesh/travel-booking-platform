using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace TravelBooking.Modules.Sample.Endpoints;

internal static class ValidateSampleEndpoint
{
    // Reached only when the request passed validation; invalid requests get a 400 ValidationProblem first.
    public static Ok<SampleResponse> Handle(SampleRequest request) =>
        TypedResults.Ok(new SampleResponse(request.Name!, request.Quantity));
}

internal sealed record SampleResponse(string Name, int Quantity);
