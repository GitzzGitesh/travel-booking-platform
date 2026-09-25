using System.ComponentModel.DataAnnotations;
using TravelBooking.Modules.Sample.Endpoints;

namespace TravelBooking.Modules.Sample.UnitTests;

public sealed class SampleRequestTests
{
    [Fact]
    public void Cross_field_rule_rejects_To_before_From()
    {
        var request = new SampleRequest { Name = "a", Quantity = 1, From = new DateOnly(2026, 10, 2), To = new DateOnly(2026, 10, 1) };

        var results = request.Validate(new ValidationContext(request)).ToList();

        results.ShouldHaveSingleItem().MemberNames.ShouldBe([nameof(SampleRequest.To)]);
    }

    [Fact]
    public void Cross_field_rule_accepts_To_on_From()
    {
        var day = new DateOnly(2026, 10, 1);
        var request = new SampleRequest { Name = "a", Quantity = 1, From = day, To = day };

        request.Validate(new ValidationContext(request)).ShouldBeEmpty();
    }
}
