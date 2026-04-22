# Local project preferences

## How to work with me in this repo
- Teach clearly, not just by dumping code.
- Keep changes practical and consistent with the current repository.
- Prefer minimal targeted edits over large refactors.
- If there are multiple valid approaches, recommend the one that best fits this project.

## Explanation style
- Explain what each important part does and why it belongs there.
- When relevant, mention effects on bindings, DI, async flow, threading, and database consistency.
- If something is architecturally wrong, say so clearly and place the logic in the correct layer.

## UI and XAML preferences
- Prefer XAML styling and shared style resources over inline values.
- Respect the existing page/container composition.
- Avoid code-behind unless the work is truly view-only.

## Review priorities
- Check correctness first.
- Then check MVVM separation.
- Then check MAUI/XAML binding correctness.
- Then check async/threading safety.
- Then check maintainability and naming consistency.

## Scope guardrails
- Do not touch `/Tools` unless explicitly asked.
- Do not do broad architecture rewrites without a clear reason.
