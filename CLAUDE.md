# KieshStockExchange

## Project overview
- This is a .NET MAUI stock exchange simulation using MVVM, SQLite, and service-based architecture.
- Primary development target is Windows.
- Prefer extending the current architecture over replacing it.

## Build and run
```bash
dotnet build KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0
dotnet run --project KieshStockExchange/KieshStockExchange.csproj -f net9.0-windows10.0.19041.0
```
Build the client csproj (not the solution): the Windows TFM only applies to the MAUI project; the `KieshStockExchange.Shared` class library targets `net9.0` and is pulled in via ProjectReference at its native TFM.

## Testing
- Testing is mainly manual through the running app unless explicit automated tests are added.
- Before suggesting a test strategy, inspect the repo to confirm the current testing setup.

## Architecture rules
- Keep UI concerns in Views and ViewModels.
- Keep business/domain logic in Models and Services.
- Keep database access in the data layer.
- Keep market data logic in MarketDataServices.
- Keep order placement, matching, and execution logic in MarketEngineServices.
- Keep portfolio mutation and holdings logic in PortfolioServices.
- Keep shared state in dedicated services such as session and selected-stock services.
- Do not move business logic into code-behind unless it is purely view-specific.

## Repo structure expectations
- Views are XAML-based and paired with ViewModels.
- Reusable UI is composed from smaller ContentViews into final ContentPages.
- Follow the existing naming and folder conventions.
- Prefer small focused classes over mixed-responsibility classes.

## Styling rules
- Prefer shared XAML styles in `Resources/Styles/`, especially `MyStyles.xaml` and related style files.
- Prefer `StaticResource` over repeated inline styling.
- Keep styling consistent with the rest of the repo.

## MAUI / MVVM rules
- Favor bindings, observable properties, and commands over code-behind event logic.
- Be careful with BindingContext assumptions, async/await flow, and UI-thread updates.
- If a bug may be caused by MAUI lifecycle, binding, or threading behavior, call that out explicitly.
- Respect existing DI and service abstractions.

## Model and domain rules
- Preserve model validation and invariants.
- Primary identifiers and immutable fields should remain immutable unless there is a deliberate model change.
- `Position` uses `Quantity` and `ReservedQuantity`.
- `Fund` uses `TotalBalance` and `ReservedBalance`.
- Order types are represented by string constants in the `Order` model, not by an enum.
- Reuse existing helpers and services instead of duplicating logic.

## Working rules for Claude
- Before proposing structural changes, read the relevant files first.
- Do not invent service names, flows, or interfaces when the current code can be inspected.
- Prefer minimal, targeted changes over broad rewrites.
- Keep naming and folder conventions consistent with the repo.
- When suggesting a fix, first identify which layer it belongs to: View, ViewModel, Model, Service, Helper, or Data layer.

## Key flows
- Order placement: `OrderEntryService` → `OrderExecutionService` → `MatchingEngine` → `SettlementEngine`
- Multi-table writes must use `IDataBaseService.RunInTransactionAsync()` (supports nested savepoints via AsyncLocal).

## Out-of-scope area
- Do not modify `/Tools` in this project unless explicitly asked.
- Treat AI bot generation scripts and related tooling as separate from normal app feature work.

## Response format for this repo
When helping with a bug or feature:
1. State what the issue is.
2. Explain why it happens.
3. Explain where the fix belongs in this architecture.
4. Give the corrected code.
5. Explain the important parts of the code.
6. Optionally suggest small improvements that fit the current structure.
