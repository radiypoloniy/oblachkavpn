---
core: dotnet-test/run-tests
binding-revision: "1"
mode: extend
---

# Oblachka repository test binding

- Run commands from the repository root containing `build.ps1` and `test.ps1`.
- Production build: `.\build.ps1`. It downloads or reuses verified cores, builds the native core, and creates `out\app\Oblachka.exe`.
- Full verification: `.\test.ps1`. If `out\app\tools\xray.exe` is absent, run `.\build.ps1` first.
- Do not substitute `dotnet test`: `tests\Oblachka.Tests.csproj` is a WinExe custom self-test controller, not a VSTest/MTP suite.
- `test.ps1` runs source-size, voice, and token checks; builds the separate test application with `IncludeTestCode=true`; runs `Oblachka.Tests.exe --self-test`; runs real-window `Oblachka.exe --ui-self-test`; and checks that production output contains no test code.
- Reports are written under `out\test\` as `self-test-*.json` and `ui-test-*.json`. Inspect failures in those reports and treat them as generated artifacts.
- No documented per-test filter exists. Do not invent VSTest or MTP filter flags; for focused development use the existing fixtures and a narrow build only as an intermediate check, then run the full repository script before completion.
- A passing build is not a passing test run. Report the counts printed by `test.ps1` for both self-tests and UI tests.
