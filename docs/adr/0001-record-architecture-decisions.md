# 0001. Record architecture decisions

- **Status:** Proposed
- **Date:** 2026-09-24
- **Related:** [`0000-template.md`](0000-template.md), `.claude/rules/workflow.md`

## Context

The platform will be built over many phases by humans and AI assistants (Claude Code). Without a durable record, decisions get re-litigated, silently reversed, or forgotten, especially across AI sessions that start without prior context.

## Decision

We will record significant architecture decisions as ADRs in `docs/adr/` using the MADR-lite template in `0000-template.md`.

- A decision is "significant" if it adds or removes a project, host, library, infrastructure component, cloud service, integration, agent/MCP server; changes module boundaries or data ownership; or changes a cross-cutting convention (security, money, time, API, testing).
- ADRs are numbered sequentially and never renumbered.
- Accepted ADRs are immutable. Changes happen through a new ADR that supersedes the old one.
- Claude Code must read relevant ADRs before changing an area, and must propose deviations with an ARCHITECTURE REVIEW block (see `CLAUDE.md`) rather than acting on them.

## Consequences

- **Positive:** shared memory for people and AI sessions; fewer silent architecture changes; easier onboarding.
- **Negative:** small overhead per decision.

## Alternatives considered

| Option | Why not chosen |
|---|---|
| Wiki/Confluence | Separate from code, not versioned with PRs, invisible to Claude Code |
| Decisions in PR descriptions only | Hard to find later; no supersession trail |
