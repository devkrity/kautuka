# Auditability ratchet decisions

Kautuka uses ratchets to keep conceptual owners understandable as the codebase grows. A line-count limit is only a backstop: a partial declaration is part of the same conceptual owner, constructor dependencies expose coupling, and public/internal method count exposes expanding authority.

## Enforced rule

Architecture tests set the current upper bounds for each named owner. They cover every partial declaration together, constructor count, constructor dependency count, and public/internal method count. The acceptance workspace applies the corresponding source-size, dependency, duplication, selector, sleep, and assertion-ownership rules.

There are **no active exceptions** to these channel/runtime ratchets. A failing ratchet is not fixed by increasing a threshold, adding a suppression, moving code to an unowned helper, or adding a partial declaration.

## Required decision before any exception can exist

An exception is not available by default. Before one may be proposed, create an ADR that names:

1. the conceptual owner and exact measured bound;
2. why a focused extraction cannot remove the added authority or dependency;
3. the one-way dependency and security impact;
4. the test evidence that protects the new boundary;
5. the removal/review date and accountable owner.

The ADR must be accepted by the product/architecture owner before code changes. It must also add a narrow architecture test documenting the decision. The decision cannot retroactively bless an already-landed threshold increase. If the decision is not accepted, refactor.

This rule does not create a generic exemption mechanism: it makes any proposed departure explicit, reviewable, time-bounded, and independently testable.
