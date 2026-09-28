# Open questions

Business and requirement decisions that engineering cannot make alone. Add questions here instead of guessing. When one is answered, record the answer and date, and link any ADR it produced.

**Blocks** names the phase-entry gate each question must pass. None of Q1–Q3, Q5, or Q6 blocks closing Phase 0; see the gates in [`../progress.md`](../progress.md#phase-entry-gates). A gate does not mean the question is answered.

| # | Question | Why it matters | Blocks | Status |
|---|---|---|---|---|
| Q1 | **Merchant-of-record model** per product: do we charge customers for flights and hotels ourselves (merchant), or does the airline/supplier charge (agency), or mixed? | Determines payment flows, PCI scope (card pass-through to airlines breaks SAQ-A), refund handling, invoicing, and legal liability | **Gate:** before payment architecture is finalised (ADR 0005/0006) and before Phase 2 payment work (`IPaymentProvider`) | **Answered for flights 2026-09-25:** we are merchant of record (see below). **Hotels: open** |
| Q2 | **Launch markets** (countries of sale and customer residency)? | Drives EU Package Travel Directive / UK ATOL / US seller-of-travel obligations, GDPR/CCPA, SCA, data residency, price-display laws, languages, currencies | **Gate:** before production-market, compliance, and hosting-region decisions, and before production-oriented design in Phase 3 | Open |
| Q3 | **First product**: flights or hotels for the first vertical slice? | Scopes the MVP. Hotels are simpler (no ticketing/APIS); flights are the harder, higher-value flow | Phase 2 product slice | **Answered 2026-09-25:** flights first (see below) |
| Q4 | **Team size and roles** now and at launch? | Affects PR review policy, CODEOWNERS, on-call, branch protection strictness | ADR 0012 review model | **Answered 2026-09-25** (see below) |
| Q5 | **Charge currencies** and FX policy: charge in the customer's currency or the supplier's? Who carries FX risk? | Money model, Stripe configuration, reconciliation | **Gate:** before real payment/currency integration (Phase 5). Produces a separate FX-policy ADR; ADR 0010's technical conventions do not wait for it | Open |
| Q6 | **Target suppliers** and their contract/commercial model (aggregator with balance, GDS with BSP/ARC accreditation, bed bank)? | Shapes provider ports and payment flows | **Gate:** before the supplier provider ports are frozen (ADR 0004) and before Phase 5 | **Answered 2026-09-27:** Amadeus is the first production flight supplier (ADR 0019); see Answered. Commercial model and credentials still pending (R1–R13) |
| Q7 | Are **combined flight + hotel orders** in scope, and when? | Package travel liability (Q2) and Order aggregate design | Phase "second product" | Open |
| Q8 | **Customer accounts**: is guest checkout allowed? | Identity flows, booking retrieval by reference + email | Phase 4 | **Answered 2026-09-26:** no guest checkout; login is required before booking (see below) |
| Q9 | **Data retention periods** for PII, bookings, and financial records? | Privacy compliance vs financial/legal retention | Data design, Phase 4 | Open |
| Q10 | **Fraud tolerance and review process**: automatic block vs manual review queue? | Checkout UX, ops staffing, Stripe Radar rules | Phase 5 | Open |
| Q11 | **Refund approval policy**: which refunds need maker-checker, and what are the thresholds? | Admin permissions and workflow | Refund phase | Open |
| Q13 | Are **group bookings (10+ passengers)** and **unaccompanied minors / child-only bookings** in scope? | The flight port limits a booking to 1–9 seated passengers with at least one adult (the usual GDS/NDC limit); both cases would need different flows | Before either flow is designed | Open. Out of scope until decided |
| Q12 | **Customer support channels** (email, chat, phone) and tooling? | Notifications, admin features, integrations | Admin phase | Open |

## Answered

### Q1. Merchant of record: flights (answered 2026-09-25)
**Answer:** **Option A. Our company is the merchant of record for flights**: we charge the customer, and the supplier is settled separately. This keeps card entry on our payment provider's hosted fields (PCI SAQ-A, ADR 0006) and supports the authorize → book → capture flow (ADR 0005). **Not decided:** the hotel model, charge currencies and FX (Q5), the payment provider's commercial terms, fraud rules (Q10), refund thresholds (Q11), and supplier settlement (Q6).

### Q6. Target flight supplier (answered 2026-09-27)
**Answer:** **Amadeus is the first production flight supplier** and the first real supplier integration. **Our company is the merchant of record** (Q1). Sabre, Travelport and Duffel remain supported future integrations, and their adapters are kept (ADR 0018). Recorded in [ADR 0019](../adr/0019-amadeus-first-production-flight-supplier.md).

**Not decided yet**, and not production-ready: the Amadeus product (Self-Service or Enterprise), credentials, the commercial agreement and ticketing party, and the supplier confirmations listed as R1–R13 in ADR 0019. No live Amadeus call has been made.

### Q8. Customer accounts (answered 2026-09-26)
**Answer:** **a customer must be signed in before proceeding to booking.** There is no guest checkout, so booking retrieval by reference, email and one-time code is not needed. Every order belongs to an authenticated customer. **Not decided:** the identity provider's tenant and configuration (ADR 0008 stays Proposed), whether search and offer selection need sign-in (today they are anonymous), and data retention (Q9).

### Q3. First product (answered 2026-09-25)
**Answer:** **flights** are the first real product slice. Phase 2 starts with the flight search port, a deterministic mock provider, and the provider contract suite (ADR 0004, ADR 0014). This answer does not settle Q1 (merchant of record), Q2 (markets), or Q6 (suppliers).

### Q4. Team size and roles (answered 2026-09-25)
**Answer:** there is currently **one developer**. The **client/business owner reviews releases**. The client/business owner is not documented as a technical code reviewer, so nobody should assume they are one. Team size **at launch** was not stated; when the team changes, revisit ADR 0012.

**Consequences** (recorded in [ADR 0012](../adr/0012-source-control-branching-and-ci.md)):
- **PR review:** every change still goes through a PR. The developer does the technical review, assisted by the `production-review` skill and the read-only reviewer agents, which are advisory. No second technical review is required while there is no second developer.
- **Branch protection:** `main` stays protected (PR required, no direct or force pushes, no deletion, linear history, no administrator bypass; CI checks required once CI exists in Phase 1). Required approvals = 0, because GitHub does not let an author approve their own PR.
- **CODEOWNERS:** not used while there is one developer. Added when a second technical owner joins. The client/business owner is not made a CODEOWNER.
- **Release review:** the client/business owner approves each production release (business scope and behaviour, not code). If that approval, or any PR approval, is to happen inside GitHub, they need their own GitHub account with the matching repository access. That access has not been granted or decided yet.

