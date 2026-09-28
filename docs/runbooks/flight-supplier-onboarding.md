# Onboarding a flight supplier

**When:** credentials or commercial approval arrive for Duffel, Amadeus, Sabre or Travelport (Q6). Amadeus is the first production supplier (ADR 0019): start there, and tick off its requirements R1–R13.
**Owner:** engineering, with the business owner for commercial items.
**Customer impact:** none until the supplier is selected for search in an environment.

## Steps
1. **Sandbox credentials:** store them in user-secrets (local) or Key Vault (Azure), never in appsettings or Git. Set `Integrations:Flights:<Name>:Enabled=true` and `BaseUrl`, plus the credential keys listed in `docs/architecture/flight-suppliers.md`.
2. **Sandbox contract:** set the sandbox environment variables and run the contract suite: `dotnet test --project tests/backend/ProviderContracts`.
   - Duffel: `DUFFEL_SANDBOX_BASE_URL`, `DUFFEL_SANDBOX_TOKEN`, `DUFFEL_API_VERSION`.
   - Amadeus: `AMADEUS_TEST_BASE_URL` (the Amadeus test environment, ending in `/`), `AMADEUS_TEST_CLIENT_ID`, `AMADEUS_TEST_CLIENT_SECRET`. Record the Amadeus error codes seen in failures; they settle R13.
   - Tests without credentials report as skipped, never passed.
   - Fix the mapping where real responses differ from the documentation-shaped fixtures, and update the fixtures to real (anonymised) shapes.
3. **Capabilities:** confirm each "requires confirmation" item with the supplier. Record it in code, or override it by configuration (`Capabilities:<Name>`), and update the matrix. Raise the stage to SandboxVerified only when the contract passes.
4. **Booking:**
   - Map booking and lookup by OUR reference. If the supplier cannot look up by our reference, the adapter must keep the supplier's reference before returning, which needs an ADR (provider-integration.md).
   - Add the operations to the adapter's implemented set only when the booking half of the contract passes in the sandbox.
5. **Before Production:**
   - Write the supplier ADR (commercial model, merchant model, idempotency, lookup, sandbox, the not-found consistency window).
   - Add the read-only resilience pipeline at the marked hook (ADR 0003).
   - Get commercial approval, then set the stage to ProductionReady.
   - Startup refuses anything less outside Development and Staging.
6. **Select for search:** set `Flights:SearchProviderId=<id>` when more than one provider is composed. Startup refuses a provider that does not implement search.

## Do NOT
- Do not mark a capability Supported, or raise a stage, without evidence (documentation, sandbox contract, or a written supplier confirmation).
- Do not enable an adapter in Production below ProductionReady. Startup blocks it; don't work around that.
- Do not retry booking, ticketing or cancellation calls. An unknown outcome is reconciled by lookup.

## Escalate
Engineering lead. For commercial or accreditation questions, the business owner.
