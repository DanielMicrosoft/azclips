# Agent — Agent Guide

Scoped guidance for the AI agent under `Agent/` and its tests. Read this before
changing Agent code or Agent tests. When a rule here changes, update this guide in
the same PR.

## Tests

- Build expected paths from the product's path constants (for example `HomePaths`),
  not from string literals. A literal path can drift from what the product writes, and
  the assertion then passes for the wrong reason.
- Prove that a new assertion fails when the condition it guards is broken.
- Do not write tests for test helpers. Keep fakes, harnesses and fake servers simple
  enough to verify by reading. Add a guard to a helper only when a real test needs it.
  If a helper needs its own tests, simplify it instead.
- Tests must not depend on machine speed. Do not assert an upper bound on elapsed time.
  Product code with a time budget takes a `TimeProvider` (default `TimeProvider.System`);
  its tests drive the budget with `FakeTimeProvider`. A wait for something that must
  happen uses a 60-second hang guard. Use a short window only to show that something
  did not happen. Mark any test that is still speed-dependent `[Trait("Category", "LocalOnly")]`.
