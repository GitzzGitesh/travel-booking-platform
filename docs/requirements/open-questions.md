# Open questions

Business and requirement decisions that engineering cannot make alone. Add questions here instead of guessing. When one is answered, record the answer and date, and link any ADR it produced.

**Blocks** names the phase-entry gate each question must pass. None of Q1–Q3, Q5, or Q6 blocks closing Phase 0; see the gates in [`../progress.md`](../progress.md#phase-entry-gates). A gate does not mean the question is answered.

| # | Question | Why it matters | Blocks | Status |
|---|---|---|---|---|
| Q1 | **Merchant-of-record model** per product: do we charge customers for flights and hotels ourselves (merchant), or does the airline/supplier charge (agency), or mixed? | Determines payment flows, PCI scope (card pass-through to airlines breaks SAQ-A), refund handling, invoicing, and legal liability | **Gate:** before payment architecture is finalised (ADR 0005/0006) and before Phase 2 payment work (`IPaymentProvider`) | Open |
| Q2 | **Launch markets** (countries of sale and customer residency)? | Drives EU Package Travel Directive / UK ATOL / US seller-of-travel obligations, GDPR/CCPA, SCA, data residency, price-display laws, languages, currencies | **Gate:** before production-market, compliance, and hosting-region decisions, and before production-oriented design in Phase 3 | Open |
| Q3 | **First product**: flights or hotels for the first vertical slice? | Scopes the MVP. Hotels are simpler (no ticketing/APIS); flights are the harder, higher-value flow | **Gate:** before implementation priority is finalised and before the Phase 2 product slice (which provider port and mock come first) | Open. Engineering has no strong preference; flights de-risk the harder model first |
| Q4 | **Team size and roles** now and at launch? | Affects PR review policy, CODEOWNERS, on-call, branch protection strictness | ADR 0012 review model | **Answered 2026-09-25** (see below) |
| Q5 | **Charge currencies** and FX policy: charge in the customer's currency or the supplier's? Who carries FX risk? | Money model, Stripe configuration, reconciliation | **Gate:** before real payment/currency integration (Phase 5). Produces a separate FX-policy ADR; ADR 0010's technical conventions do not wait for it | Open |
| Q6 | **Target suppliers** and their contract/commercial model (aggregator with balance, GDS with BSP/ARC accreditation, bed bank)? | Shapes provider ports and payment flows | **Gate:** before the supplier provider ports are frozen (ADR 0004) and before Phase 5 | Open |
| Q7 | Are **combined flight + hotel orders** in scope, and when? | Package travel liability (Q2) and Order aggregate design | Phase "second product" | Open |
| Q8 | **Customer accounts**: is guest checkout allowed? | Identity flows, booking retrieval by reference + email | Phase 4 | Open |
| Q9 | **Data retention periods** for PII, bookings, and financial records? | Privacy compliance vs financial/legal retention | Data design, Phase 4 | Open |
| Q10 | **Fraud tolerance and review process**: automatic block vs manual review queue? | Checkout UX, ops staffing, Stripe Radar rules | Phase 5 | Open |
| Q11 | **Refund approval policy**: which refunds need maker-checker, and what are the thresholds? | Admin permissions and workflow | Refund phase | Open |
| Q12 | **Customer support channels** (email, chat, phone) and tooling? | Notifications, admin features, integrations | Admin phase | Open |

## Answered

### Q4. Team size and roles (answered 2026-09-25)
**Answer:** there is currently **one developer**. The **client/business owner reviews releases**. The client/business owner is not documented as a technical code reviewer, so nobody should assume they are one. Team size **at launch** was not stated; when the team changes, revisit ADR 0012.

**Consequences** (recorded in [ADR 0012](../adr/0012-source-control-branching-and-ci.md)):
- **PR review:** every change still goes through a PR. The developer does the technical review, assisted by the `production-review` skill and the read-only reviewer agents, which are advisory. No second technical review is required while there is no second developer.
- **Branch protection:** `main` stays protected (PR required, no direct or force pushes, no deletion, linear history, no administrator bypass; CI checks required once CI exists in Phase 1). Required approvals = 0, because GitHub does not let an author approve their own PR.
- **CODEOWNERS:** not used while there is one developer. Added when a second technical owner joins. The client/business owner is not made a CODEOWNER.
- **Release review:** the client/business owner approves each production release (business scope and behaviour, not code). If that approval, or any PR approval, is to happen inside GitHub, they need their own GitHub account with the matching repository access. That access has not been granted or decided yet.

