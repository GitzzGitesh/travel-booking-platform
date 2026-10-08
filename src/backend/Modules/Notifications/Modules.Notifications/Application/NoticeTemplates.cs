using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TravelBooking.Modules.Notifications.Application;

/// <summary>The non-personal facts a booking notice shows (stored as JSON on the notification).</summary>
/// <summary>A booked hotel stay's non-personal facts, as stored on the notice (the voucher, ADR 0030).</summary>
/// <param name="FreeUntil">The free-cancellation deadline (an instant), shown in the hotel's <paramref name="TimeZone"/>.</param>
internal sealed record StayNotice(
    string Hotel, string Address, DateOnly CheckIn, DateOnly CheckOut, int Nights, string Room, string Board, bool Refundable, DateTimeOffset? FreeUntil,
    string? PenaltyAmount, string? PenaltyCurrency, string TimeZone);

internal sealed record BookingNoticeValues(
    Guid OrderId, IReadOnlyList<string> BookingReferences, string? ChargedAmount, string? ChargedCurrency, IReadOnlyList<StayNotice>? Stays = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    public static BookingNoticeValues FromJson(string json) =>
        JsonSerializer.Deserialize<BookingNoticeValues>(json, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("Empty notice values.");
}

internal sealed record RenderedNotice(string Subject, string HtmlBody, string TextBody);

/// <summary>
/// The customer notices (ADR 0024): typed templates in code, HTML and plain text, every value HTML-encoded. Texts are per
/// culture (English first; a language is added as another <see cref="NoticeTexts"/>). Amounts and references come from
/// the server-side snapshot as they were stored, never recomputed. Never personal data beyond the recipient address, and
/// never document numbers, card data or supplier messages.
/// </summary>
internal static class NoticeTemplates
{
    public const string BookingConfirmed = "booking-confirmed";
    public const string BookingPartiallyConfirmed = "booking-partially-confirmed";
    public const string BookingNotBooked = "booking-not-booked";
    public const string RefundCompleted = "refund-completed";
    public const string BookingCancelled = "booking-cancelled";
    public const string RefundDelayed = "refund-delayed";
    public const string CancellationRequested = "cancellation-requested";
    public const string CancellationDeclined = "cancellation-declined";

    /// <summary>Bumped when a template's meaning changes; stored on each notification.</summary>
    public const int Version = 1;

    public const string DefaultCulture = "en";

    private static readonly Dictionary<string, NoticeTexts> _texts = new(StringComparer.Ordinal) { ["en"] = NoticeTexts.English };

    public static bool IsKnown(string kind) => kind is BookingConfirmed or BookingPartiallyConfirmed or BookingNotBooked or RefundCompleted or BookingCancelled or RefundDelayed
        or CancellationRequested or CancellationDeclined;

    public static RenderedNotice Render(string kind, string culture, BookingNoticeValues values)
    {
        var texts = _texts.GetValueOrDefault(culture) ?? _texts[DefaultCulture];
        var (subject, intro) = kind switch
        {
            BookingConfirmed => (texts.ConfirmedSubject, texts.ConfirmedIntro),
            BookingPartiallyConfirmed => (texts.PartialSubject, texts.PartialIntro),
            BookingNotBooked => (texts.NotBookedSubject, texts.NotBookedIntro),
            RefundCompleted => (texts.RefundSubject, texts.RefundIntro),
            BookingCancelled => (texts.CancelledSubject, texts.CancelledIntro),
            RefundDelayed => (texts.RefundDelayedSubject, texts.RefundDelayedIntro),
            CancellationRequested => (texts.CancellationRequestedSubject, texts.CancellationRequestedIntro),
            CancellationDeclined => (texts.CancellationDeclinedSubject, texts.CancellationDeclinedIntro),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown notice."),
        };

        var lines = new List<(string Label, string Value)> { (texts.OrderLabel, values.OrderId.ToString("D", CultureInfo.InvariantCulture)) };
        if (values.BookingReferences.Count > 0)
        {
            lines.Add((texts.ReferencesLabel, string.Join(", ", values.BookingReferences)));
        }

        // The voucher: each booked stay, as booked (confirmations only; a cancellation or refund notice repeats none).
        if (kind is BookingConfirmed or BookingPartiallyConfirmed)
        {
            foreach (var stay in values.Stays ?? [])
            {
                lines.AddRange(StayLines(stay, texts));
            }
        }

        // A cancellation promises no amount: the refund, if any, still needs a second person's approval (ADR 0027).
        var amountLabel = kind switch
        {
            RefundCompleted => texts.RefundedLabel,
            RefundDelayed => texts.RefundAmountLabel,
            BookingCancelled or CancellationRequested or CancellationDeclined => null,
            _ => texts.ChargedLabel,
        };
        if (amountLabel is not null)
        {
            lines.Add(values.ChargedAmount is { } amount && values.ChargedCurrency is { } currency
                ? (amountLabel, $"{amount} {currency}")
                : (amountLabel, texts.NothingCharged));
        }

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"").Append(Encode(texts.Language)).Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(Encode(subject)).Append("</title></head><body><p>").Append(Encode(intro)).Append("</p><table role=\"presentation\">");
        foreach (var (label, value) in lines)
        {
            html.Append("<tr><th scope=\"row\" style=\"text-align:start;padding-inline-end:1em\">").Append(Encode(label))
                .Append("</th><td>").Append(Encode(value)).Append("</td></tr>");
        }

        html.Append("</table><p>").Append(Encode(texts.Footer)).Append("</p></body></html>");

        var text = new StringBuilder().AppendLine(intro).AppendLine();
        foreach (var (label, value) in lines)
        {
            text.Append(label).Append(": ").AppendLine(value);
        }

        text.AppendLine().Append(texts.Footer);
        return new RenderedNotice(subject, html.ToString(), text.ToString());
    }

    private static IEnumerable<(string Label, string Value)> StayLines(StayNotice stay, NoticeTexts texts)
    {
        var culture = CultureInfo.GetCultureInfo(texts.Language);
        yield return (texts.HotelLabel, $"{stay.Hotel}, {stay.Address}");
        yield return (texts.StayLabel, string.Format(culture, texts.StayFormat, stay.CheckIn.ToString(texts.DateFormat, culture), stay.CheckOut.ToString(texts.DateFormat, culture), stay.Nights));
        yield return (texts.RoomLabel, $"{stay.Room}, {texts.Board(stay.Board)}");
        yield return (texts.CancellationLabel, Cancellation(stay, texts, culture));
    }

    // The deadline in the hotel's own time zone, labelled; the penalty as stored (never recomputed).
    private static string Cancellation(StayNotice stay, NoticeTexts texts, CultureInfo culture)
    {
        if (!stay.Refundable || stay.FreeUntil is not { } deadline)
        {
            return texts.NonRefundable;
        }

        // In the hotel's zone, labelled; an unknown zone falls back to UTC, labelled UTC (never a mislabelled time).
        var when = TimeZoneInfo.TryFindSystemTimeZoneById(stay.TimeZone, out var zone)
            ? $"{TimeZoneInfo.ConvertTime(deadline, zone).ToString(texts.DeadlineFormat, culture)} ({stay.TimeZone})"
            : $"{deadline.ToUniversalTime().ToString(texts.DeadlineFormat, culture)} (UTC)";
        var after = stay.PenaltyAmount is { } amount && stay.PenaltyCurrency is { } currency
            ? string.Format(culture, texts.PenaltyAfter, $"{amount} {currency}")
            : texts.NothingAfter;
        return string.Format(culture, texts.FreeUntil, when, after);
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
}

/// <summary>The texts of the notices in one language.</summary>
internal sealed record NoticeTexts(
    string Language,
    string ConfirmedSubject,
    string ConfirmedIntro,
    string PartialSubject,
    string PartialIntro,
    string NotBookedSubject,
    string NotBookedIntro,
    string OrderLabel,
    string ReferencesLabel,
    string ChargedLabel,
    string NothingCharged,
    string RefundSubject,
    string RefundIntro,
    string RefundedLabel,
    string CancelledSubject,
    string CancelledIntro,
    string RefundAmountLabel,
    string RefundDelayedSubject,
    string RefundDelayedIntro,
    string CancellationRequestedSubject,
    string CancellationRequestedIntro,
    string CancellationDeclinedSubject,
    string CancellationDeclinedIntro,
    string Footer,
    string HotelLabel = "Hotel",
    string StayLabel = "Stay",
    string StayFormat = "{0} to {1} ({2} nights)",
    string DateFormat = "dddd d MMMM yyyy",
    string DeadlineFormat = "d MMMM yyyy, HH:mm",
    string RoomLabel = "Room",
    string CancellationLabel = "Cancellation",
    string NonRefundable = "Non-refundable: nothing is refunded if you cancel.",
    string FreeUntil = "Free cancellation if you ask before {0}; {1}.",
    string PenaltyAfter = "after that, {0} is kept",
    string NothingAfter = "after that, nothing is refunded")
{
    public string Board(string board) => board switch
    {
        "RoomOnly" => "room only",
        "Breakfast" => "breakfast included",
        "HalfBoard" => "half board",
        "FullBoard" => "full board",
        "AllInclusive" => "all inclusive",
        _ => board,
    };

    public static readonly NoticeTexts English = new(
        "en",
        "Your booking is confirmed",
        "Your booking is confirmed. Keep the booking reference for your trip.",
        "Part of your booking is confirmed",
        "Part of your booking is confirmed. You are charged only for what is booked; the rest of the amount held on your card is being released.",
        "We could not complete your booking",
        "We could not complete your booking. Nothing was charged, and the amount held on your card is being released (your bank may take a few days to show it).",
        "Order",
        "Booking reference",
        "Charged",
        "Nothing",
        "Your refund has been sent",
        "Your refund has been sent to the card you paid with. Your bank may take a few days to show it.",
        "Refunded",
        "Your booking has been cancelled",
        "Your booking has been cancelled. If a refund is due, our team checks it first and sends it to the card you paid with; we will email you when it is sent.",
        "Refund amount",
        "Your refund is delayed",
        "We could not complete your refund yet. Our team is looking into it and will contact you; you do not need to do anything.",
        "We have received your cancellation request",
        "We have received your request to cancel your booking. Our team handles it with the airline or hotel; your booking stays as it is until we confirm the cancellation by email.",
        "We could not cancel your booking",
        "We could not cancel your booking as requested. Your booking stays as it is, and our support team will contact you about it.",
        "This is an automated message about your order. Your trip details are in your account.");
}
