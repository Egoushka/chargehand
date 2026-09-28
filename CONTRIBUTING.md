# Contributing

Thanks for looking. The project is pre-alpha; open an issue before a large change.

## Ground rules

- **Commits** follow [Conventional Commits](https://www.conventionalcommits.org/):
  `type(scope)?: subject`, types `feat fix docs style refactor perf test build ci chore revert`.
  A `commit-msg` hook and CI enforce it.
- **Decisions** that change architecture get an ADR in `docs/adr/` (copy `template.md`).
- **Schemas** are versioned by major in the path (`schemas/result/v1/`). A breaking change is a
  new major directory, never an edit of a published one. A test compares every schema with the latest `v*` tag and
  fails on a removed property, a new required one, a narrowed type or enum, or a tighter constraint.
- **No private configuration in the repository.** Code talks to an OpenCode server URL and uses
  `provider/model` ids from a profile. Your profile goes in `profiles/local.json` (gitignored);
  `profiles/example.json` holds placeholders only. Secrets are referenced by name, never stored.

## Sign-off

Contributions come under the [Developer Certificate of Origin](https://developercertificate.org/). Sign off every
commit with `git commit -s`; the `Signed-off-by` line certifies that you wrote the change or may submit it under the
project's license.

## Setup

```bash
git config core.hooksPath .githooks
cp .private-terms.example .private-terms   # then list your own private terms
```

The `pre-commit` hook needs [gitleaks](https://github.com/gitleaks/gitleaks) and blocks commits
that match `.private-terms`. Don't bypass it with `--no-verify`.

## Build and test

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```
