# Contributing to Kautuka

Kautuka is licensed under Apache-2.0.

Contributions are welcome. Begin with the [developer guide](docs/development/README.md), then use the [documentation index](docs/README.md) to find the architecture and engineering guidance for the area you intend to change.

## Compatibility and data changes

Kautuka is released software. Changes must preserve backward compatibility for supported versions and protect existing user data. Schema and protocol changes require a migration path and compatibility tests. Do not make forward-only changes unless maintainers approve a compelling security or technical reason and the pull request includes a migration or replacement plan and release notes.

- Database changes must include a versioned migration from supported prior schemas, with upgrade and recovery behavior covered by tests and documented where operator action is required.
- Public APIs, persisted formats, event contracts, and configuration must remain backward compatible when feasible. If compatibility cannot be maintained, provide a transition path and explain the impact.
- Breaking changes require explicit maintainer approval, a compelling technical or security reason, a migration or replacement plan, and clear release notes. A major-version release may be required; do not assume a breaking change is acceptable because it simplifies implementation.
- Never silently discard, reinterpret, or overwrite user data during an upgrade.

## Engineering principles

- Change the service or package that owns the behavior; avoid duplicating authority in projections or clients.
- Keep canonical state distinct from derived UI, delivery, and cache state.
- Preserve cancellation, durability, access control, and audit behavior across failure and restart paths.
- Keep changes focused and make operational consequences reviewable.
- Update architecture and user documentation when responsibilities or observable behavior change.

## DCO sign-off

Every commit must include a Developer Certificate of Origin sign-off. Use `git commit -s` so Git adds the sign-off line with your configured name and email. Commits without a valid sign-off cannot be accepted.

## Pull requests

1. Branch from the target branch documented for the repository or issue.
2. Describe the problem, proposed change, compatibility impact, data migration, and operational risks in the pull request.
3. Include tests for changed behavior and migration paths. Update relevant docs and release notes. Never include secrets, credentials, or real user data.
4. Sign every commit with `git commit -s` and leave repository hooks enabled.
5. Run the applicable backend, frontend, browser, and documentation checks. Include results and explain any checks that could not be run.
6. Request review and wait for required CI and maintainer approval before merge.

## Developer Certificate of Origin

Sign off every commit with `git commit -s` to certify the Developer
Certificate of Origin (DCO) 1.1 in [DCO](DCO). If your latest commit is missing
its sign-off, fix it with `git commit --amend -s`. To sign off a series of
commits, run `git rebase --signoff main`.

## Validation

- [ ] The change belongs at the owning service or package boundary.
- [ ] Tests and documentation cover changed behavior and supported upgrade paths.
- [ ] Persisted schema, API, event, and configuration compatibility has been assessed.
- [ ] Any unavoidable breaking change has maintainer approval, a migration plan, and release notes.
- [ ] `npm run docs:check` passes when documentation changes.
- [ ] No secrets, tokens, internal identifiers, or personal data were added.
- [ ] All commits include DCO sign-off (`git log -1 --format=%B`).
- [ ] Relevant checks pass, or limitations are explained.
