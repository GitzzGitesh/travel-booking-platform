# 0024. Notifications module and transactional email

- **Status:** Accepted (2026-10-02), under the decision authority the project owner delegated on 2026-10-02
- **Date:** 2026-10-02
- **Deciders:** Engineering lead (delegated authority)
- **Related:** [0002](0002-modular-monolith-and-module-boundaries.md), [0004](0004-supplier-provider-abstraction.md), [0007](0007-async-processing-worker-and-outbox.md), [0020](0020-traveller-personal-data-store-and-retention.md), [0021](0021-booking-orchestration-supplier-booking-and-capture.md), Q12 (support channels)

## Context
Customers get no message after checkout today: the outcome exists only in the API response and the order. Booking confirmations, the customer consent behind Q15, and the cancellation and refund notices behind Q11 all need email. The architecture overview already names a Notifications module that the Worker runs, but none exists, and no email provider is chosen.

## Decision
1. **A new module, `Modules.Notifications`** (schema `notifications`). Its `Contracts` project is added when another module first needs one; none does yet. It owns notification records, templates and delivery state. It reacts to other modules' integration events through the outbox and inbox (ADR 0007), and never decides business outcomes.
2. **Provider-neutral port `IEmailSender`.** It sends one message, carrying our message id, and returns `Accepted(providerMessageId)`, `Rejected(reason)` or `Unknown`. Adapters sit behind the port. The first is a deterministic in-process **recording sender** inside the module (no network), used in Development and Staging and refused in any other environment, like the payment mock. Real providers are `Integrations.Email.*` projects (ADR 0004).
3. **Provider: Azure Communication Services (ACS) Email.** It is the same cloud as ADR 0003, uses managed identity (no API key), has the lowest cost, and reports delivery through Event Grid.
   - **Fallback:** a dedicated transactional email provider (Postmark), if bounce or inbox metrics disappoint. With the port, that is one adapter.
   - **When it is built:** the ACS adapter comes once the Azure resource and the sending domain exist, which are external. No ACS contract or limit is assumed; the initial sending limits must be requested from Azure before launch.
4. **At least once, never lost.** A notification is decided by an integration event that is saved with the business change itself (for example `OrderBookingSettled`).
   - **One record per (source event, kind):** a unique constraint makes a redelivered event a no-op.
   - **Sending:** a Worker job sends records with backoff (1, 2, 4 … minutes, at most 10 attempts).
   - **Unknown outcome:** `Unknown` is retried. A rare duplicate email is accepted, and a missing booking confirmation is not, so delivery is at least once.
   - **Giving up:** after the last attempt the record is `Failed`, with the `NotificationFailed` alert.
5. **Delivery states:** `Pending → Sending → Accepted → Delivered | Bounced | Suppressed | Failed`.
   - **Before acceptance:** `Pending` covers waiting for a retry.
   - **Provider delivery events:** they move `Accepted` forward once the provider adapter exists. Until then, `Accepted` is final.
   - **A hard bounce:** it raises an operations alert, because the customer may need another channel.
6. **Templates in code.** Typed C# templates render HTML and plain text, HTML-encoding every value. No template engine, and no provider-hosted templates (lock-in).
   - **Texts:** per culture, English first, with the culture chosen per message so other languages can follow (Q2).
   - **Values:** amounts, references and times come from the event's server-side snapshot, never recomputed.
   - **Never in an email:** document numbers, card data, or supplier messages.
7. **Personal data minimised (ADR 0020).** The recipient address is read from the Customers module (`IOrderContacts`) when sending, and never stored in the notifications schema.
   - **What is stored:** the template id and version, and the non-personal values (order id, references, amounts).
   - **When the address is gone:** if the order's personal data is anonymised, there is no address, and the record ends `Suppressed`.
8. **Sender structure.** Messages come from a dedicated sending subdomain, `Notifications:FromAddress` (for example `notifications@mail.<brand domain>`), with `Notifications:ReplyTo` pointing to support (Q12). Both are configuration, set per environment.
   - **DNS needed before production (external):** SPF and DKIM for the subdomain (ACS verifies both), and DMARC on the brand domain. Start DMARC at `p=none` with reporting, and move to `quarantine` once the reports are clean.
9. **Operational alerts are not customer email.** They stay as log events and metrics (Azure Monitor alerts).

## Consequences
**Positive**
- Every booking outcome produces exactly one confirmation, failure or release message.
- Retrying is safe.
- The provider can be replaced through one adapter.
- No personal data is stored twice.
- The notifications table is also the module's inbox: the unique (source event, kind) makes a redelivered event a no-op, so there is no separate inbox table. A template kind is therefore never renamed while events for it may still be in flight.

**Negative / trade-offs**
- One new project.
- Emails show references and amounts, not the full itinerary, until a Flights contract exposes the itinerary snapshot (a follow-up).
- ACS deliverability is to be measured, with the switch criteria in point 3.

**Follow-ups (external)**
- The ACS resource, the sending limits and domain verification.
- DNS records.
- The support address (Q12).

## Alternatives considered
| Option | Why not chosen |
|---|---|
| Send email inline from Orders | Couples a slow external call to the booking transaction, and loses messages on a crash |
| A dedicated provider first (Postmark, SendGrid) | Strong deliverability, but a second vendor and an API key. Kept as the fallback |
| A template engine or provider templates | A new dependency or lock-in, for a handful of plain templates |
