namespace TravelBooking.BuildingBlocks;

/// <summary>The outcome of an operation with an expected failure mode (backend rules: expected failures are results).</summary>
public sealed class Result<TValue, TError>
    where TValue : notnull
    where TError : notnull
{
    private readonly TValue? _value;
    private readonly TError? _error;

    private Result(TValue value)
    {
        _value = value;
        IsSuccess = true;
    }

    private Result(TError error) => _error = error;

    public bool IsSuccess { get; }

    public TValue Value => IsSuccess ? _value! : throw new InvalidOperationException("The result is a failure and has no value.");

    public TError Error => IsSuccess ? throw new InvalidOperationException("The result is a success and has no error.") : _error!;

    public static Result<TValue, TError> Success(TValue value) => new(value);

    public static Result<TValue, TError> Failure(TError error) => new(error);
}
