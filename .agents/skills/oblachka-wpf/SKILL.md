---
name: oblachka-wpf
description: Develop and review the Oblachka Windows WPF client while preserving its code-behind architecture, design tokens, privacy rules, and existing verification workflow. Use for C#, XAML, views, MainWindow partials, traffic statistics, and desktop UI changes in this repository.
type: prompt
whenToUse: When writing, modifying, or reviewing Oblachka WPF client code, especially MainWindow, views, design tokens, traffic analytics, and UI behavior
---

# Oblachka WPF

Project instructions and the surrounding code take precedence over generic WPF advice. Make the smallest complete change; do not turn a feature into an architectural migration.

## Locate the relevant code first

- Shell and island: `src/MainWindow.xaml`, `src/Window/MainWindow.Layout.cs`, `src/Window/MainWindow.Navigation.cs`.
- Sidebar: `src/Views/HomeView.xaml`; shared navigation styles: `src/Sidebar.xaml`.
- Island views and their code-behind: `src/Views/`.
- Window behavior: the focused partial in `src/Window/MainWindow.*.cs`.
- Traffic and connections: `src/Window/MainWindow.Traffic.cs`, `src/Network/TrafficMeter.cs`, `src/Network/CoreConnections.cs`.
- Persistence: `src/Window/MainWindow.Persistence.cs` and the existing state-store types.
- Tests and real-window fixtures: `tests/`, `src/Window/Fixtures/`.

Read the target, its nearest sibling, and the covering fixture/test. Reuse existing controls and patterns before introducing new ones.

## Preserve project conventions

- Keep the current `MainWindow` partial-class and view code-behind structure. Do not rewrite a feature to MVVM, add a DI container, or introduce an MVVM package.
- Match existing naming, file organization, compact C# idioms, nullable handling, and Russian UI wording.
- Do not add packages, frameworks, telemetry, or broad refactors unless the user asks.
- Keep UI responsive: use the established timers and Dispatcher patterns, do not block on async work, and avoid expensive visual-tree rebuilds on frequent updates.
- Preserve `AutomationProperties.Name` and keyboard behavior used by the current UI tests and assistive technology.
- Statistics stay local. Do not add program/site history or traffic details to support reports or remote telemetry without an explicit request.

## Design system

- The source of truth is `theme/tokens.json`.
- After changing tokens, run `.\scripts\tokens.ps1`.
- Do not hand-edit generated `src/Themes/Light.xaml`, `Dark.xaml`, `Tokens.xaml`, or `theme/tokens.css`.
- In XAML use `{DynamicResource ...}` for theme-dependent brushes and `{StaticResource ...}` for font sizes and radii. In C# use the existing `Tokens` helpers.
- Reuse styles from `src/Design.xaml`, `src/Sidebar.xaml`, and the nearest view. Do not hard-code colors, font sizes, or corner radii.
- Check both themes and the real island/sidebar dimensions; narrow-window behavior belongs in the existing layout pattern.

## Tests and delivery

- Production build: `.\build.ps1` from the repository root.
- Full verification: `.\test.ps1` after `.\build.ps1`; this repository uses custom self-tests, not plain `dotnet test`.
- Add or extend existing self-tests/UI fixtures for new behavior. Test files compile only with `IncludeTestCode=true`; never include fixtures in the production build.
- Exercise the real window through the existing `--ui-self-test` or `--preview` facilities. A successful build alone does not verify the UI.
- Review generated reports and files under `out/`; they are test artifacts, not source changes.
- Before reporting completion, run the required build/tests and check `git status` for unintended files.

## Reference

Adapted selectively from the generic WPF skill: https://github.com/managedcode/dotnet-skills/blob/main/catalog/Frameworks/WPF/skills/wpf/SKILL.md. Its MVVM-first workflow is intentionally not applied here.
