# Contributing to ENFAStepLexer-StepParser

Thank you for your interest in contributing! This document covers the workflow, code standards, and conventions used in this repository.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet --version` should show 8.0.x or later)
- Git (with full history for GitVersion: clone normally, no shallow clones)

## Getting Started

```bash
git clone https://github.com/DevelApp-ai/ENFAStepLexer-StepParser.git
cd ENFAStepLexer-StepParser
dotnet restore
dotnet build
dotnet test
```

To try the libraries end-to-end, run the demo application:

```bash
dotnet run --project src/ENFAStepLexer.Demo
```

## Project Structure

| Project | Purpose |
|---|---|
| `src/DevelApp.StepLexer` | Zero-copy UTF-8 lexer library (NuGet package) |
| `src/DevelApp.StepParser` | GLR-style grammar parser library (NuGet package) |
| `src/DevelApp.StepLexer.Tests` | Lexer unit tests and benchmarks |
| `src/DevelApp.StepParser.Tests` | Parser unit tests |
| `src/ENFAStepLexer.Demo` | Demo console application |

## Code Standards

Both libraries are compiled with strict settings — your code must satisfy them:

- **Nullable reference types** are enabled (`<Nullable>enable</Nullable>`).
- **Warnings are errors** (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).
- **XML doc comments are required on public APIs** (missing docs fail the build via `CS1591`).
- Target framework is **net8.0**; use modern C# (`<LangVersion>latest</LangVersion>`).
- Prefer zero-copy patterns (`ReadOnlyMemory<byte>`, `ZeroCopyStringView`) in the lexer; avoid unnecessary allocations.

## Testing

- Tests use **xUnit**; benchmarks use **BenchmarkDotNet** (`src/DevelApp.StepLexer.Tests/PerformanceBenchmarks.cs`).
- Run the full suite before opening a PR: `dotnet test`.
- New features should come with tests; bug fixes should come with a regression test.

## Branching & Versioning

Versioning is automated with [GitVersion](https://gitversion.net/) (see `GitVersion.yml`):

- `main` — protected; merge target for releases. Each merge increments the **minor** version.
- `develop` — integration branch (alpha pre-releases).
- `feature/*`, `hotfix/*`, `release/*` — conventional branch names recognized by GitVersion.

Use descriptive branch names, e.g. `feature/inline-modifiers` or `hotfix/grammar-loader-crash`.

## Pull Requests

1. Fork or branch from `main` (or `develop` for early-stage work).
2. Make your changes with tests and XML docs.
3. Ensure `dotnet build` and `dotnet test` pass locally in **both Debug and Release** (CI builds both).
4. Open a PR against `main` (or `develop`). CI will:
   - Build and test in Debug and Release with code coverage,
   - Run a vulnerability scan on dependencies,
   - Publish context-aware pre-release packages to GitHub Packages (beta for PRs to `main`, alpha otherwise).
5. Merges to `main` trigger the CD pipeline, which publishes release packages to NuGet.org and creates a GitHub Release — keep this in mind for breaking changes.

### Commit Messages

Use a concise summary line plus an explanatory body when useful:

```
Add inline modifier support (?i) to StepLexer

- Parse (?i) at pattern start and per-group
- Route modifier state through PatternParser
- Add 12 tests covering case-insensitive matching
```

Reference issues in the body (`Closes #123`) so they auto-close on merge.

## AI-Assisted Contributions

Much of this repository's history was produced with the GitHub Copilot coding agent and reviewed by the maintainer. AI-assisted PRs are welcome, but the same standards apply: passing tests, XML docs, and a human reviewing before merge.

## Reporting Bugs & Feature Requests

Open a [GitHub issue](https://github.com/DevelApp-ai/ENFAStepLexer-StepParser/issues/new/choose) with:

- For bugs: minimal reproduction, expected vs. actual behavior, .NET version.
- For features: the use case and which component it affects (StepLexer / StepParser / grammar DSL).

Roadmap work is tracked in issues labeled `roadmap`.

## License

By contributing, you agree that your contributions will be licensed under the repository's [AGPL-3.0-or-later license](LICENSE).
