namespace TravelBooking.BuildingBlocks.UnitTests;

public sealed class ResultTests
{
    [Fact]
    public void Success_exposes_the_value_and_no_error()
    {
        var result = Result<int, string>.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_exposes_the_error_and_no_value()
    {
        var result = Result<int, string>.Failure("boom");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe("boom");
        Should.Throw<InvalidOperationException>(() => result.Value);
    }
}
