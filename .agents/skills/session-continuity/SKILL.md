---
name: session-continuity
description: >
  Session continuity, context ledger, function registry, and feature catalog rule for src-cloud-server.
  Updates HANDOFF.md, CONTEXT.md, FUNCTIONS.md,
  and FEATURES.md at the end of every turn to prevent duplication and
  preserve architectural memory. Trigger: /continuity, /session-continuity,
  "session continuity", or "update handoff".
---

# Session Continuity & Function Registry (Comile)

Maintain persistent session memory and enforce function and feature reuse across prompts for `Comile`.

## Operational Rules

Scope: **`/` only**.

At the end of EVERY user prompt or request:

1. **Update `HANDOFF.md`**:
   - What was completed in this turn.
   - Current application status, environment, and open ports.
   - Next steps or pending items.

2. **Update `CONTEXT.md`**:
   - Current architecture, core dependencies, styling tokens, and domain rules.
   - Recent structural changes or persistent design constraints.

3. **Update `FUNCTIONS.md`**:
   - Maintain an exhaustive catalog of shared functions, custom hooks, math utilities, context methods, and UI components.
   - **MANDATORY REUSE RULE**: Never create a duplicate function, hook, or component if one already exists. Check `FUNCTIONS.md` first before implementing new code and reuse the existing implementation.

4. **Update `FEATURES.md`**:
   - Maintain an exhaustive catalog of all features supported.
   - **ZERO DUPLICATION RULE**: Always inspect `FEATURES.md` before planning or building to avoid duplicating or re-proposing existing features.
