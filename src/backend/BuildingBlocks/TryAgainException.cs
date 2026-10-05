namespace TravelBooking.BuildingBlocks;

/// <summary>
/// A request that could not be completed right now and changed nothing (e.g. a record kept changing while it was being
/// read): the caller may simply try again. The Api answers 503 with a generic message, never the exception's details.
/// </summary>
public abstract class TryAgainException(string message) : Exception(message);
