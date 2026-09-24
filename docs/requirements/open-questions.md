# Open questions

Business and requirement decisions that engineering cannot make alone. Add questions here instead of guessing. When one is answered, record the answer and date, and link any ADR it produced.

| # | Question | Why it matters | Blocks | Status |
|---|---|---|---|---|
| Q1 | **Merchant-of-record model** per product: do we charge customers for flights and hotels ourselves (merchant), or does the airline/supplier charge (agency), or mixed? | Determines payment flows, PCI scope (card pass-through to airlines breaks SAQ-A), refund handling, invoicing, and legal liability | ADR 0006 finalisation, Phase 3 | Open |
| Q2 | **Launch markets** (countries of sale and customer residency)? | Drives EU Package Travel Directive / UK ATOL / US seller-of-travel obligations, GDPR/CCPA, SCA, data residency, price-display laws, languages, currencies | Compliance scope, Phase 3 | Open |
| Q3 | **First product**: flights or hotels for the first vertical slice? | Scopes the MVP. Hotels are simpler (no ticketing/APIS); flights are the harder, higher-value flow | Phase 3 | Open. Engineering has no strong preference; flights de-risk the harder model first |
| Q4 | **Team size and roles** now and at launch? | Affects PR review policy, CODEOWNERS, on-call, branch protection strictness | ADR 0012 details | Open |
| Q5 | **Charge currencies** and FX policy: charge in the customer's currency or the supplier's? Who carries FX risk? | Money model, Stripe configuration, reconciliation | ADR 0010 finalisation | Open |
| Q6 | **Target suppliers** and their contract/commercial model (aggregator with balance, GDS with BSP/ARC accreditation, bed bank)? | Shapes provider ports and payment flows | Phase 5, provider port design validation | Open |
| Q7 | Are **combined flight + hotel orders** in scope, and when? | Package travel liability (Q2) and Order aggregate design | Phase "second product" | Open |
| Q8 | **Customer accounts**: is guest checkout allowed? | Identity flows, booking retrieval by reference + email | Phase 4 | Open |
| Q9 | **Data retention periods** for PII, bookings, and financial records? | Privacy compliance vs financial/legal retention | Data design, Phase 4 | Open |
| Q10 | **Fraud tolerance and review process**: automatic block vs manual review queue? | Checkout UX, ops staffing, Stripe Radar rules | Phase 5 | Open |
| Q11 | **Refund approval policy**: which refunds need maker-checker, and what are the thresholds? | Admin permissions and workflow | Refund phase | Open |
| Q12 | **Customer support channels** (email, chat, phone) and tooling? | Notifications, admin features, integrations | Admin phase | Open |
