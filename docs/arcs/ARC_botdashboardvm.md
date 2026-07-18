# Arc: BotDashboardViewModel partial split (client VM)

**Target:** `KieshStockExchange/ViewModels/AdminViewModels/BotDashboardViewModel.cs` (754 LOC,
`public partial class BotDashboardViewModel : BaseViewModel, IDisposable`, namespace
`KieshStockExchange.ViewModels.AdminViewModels`). **Lane:** Auto (client, zero CK). Byte-identical partial split.
Already `partial`; base = `BaseViewModel : ObservableObject` (NO StockAware re-subscription). Auto-includes new .cs.

## Safety (Phase-0)
- Source-gen ([ObservableProperty]×~39, [RelayCommand]×10, partial OnXChanged×4) keyed by type+namespace →
  moving hand-written members between same-namespace partials is safe; no member split mid-body.
- Init-order: all field initializers are self-contained literals, no cross-field dependency, no static ctor → moot.
- XAML-transparent: BotDashboardPage.xaml (67 bindings + 2 DataTemplates) resolves by CLR namespace, not file.
  StrategyBreakdownRowVm already a sibling file — no trailing-type extraction needed.
- OnTimerTick is the one cross-concern member (fans out to the 4 refresh pipelines) → stays in spine.

## Split (spine + 3 concern partials, all same namespace)
- **`BotDashboardViewModel.cs`** (spine): _disposed; service fields (_admin,_session,_db,_stocks,_http,_logger);
  TopNavBarVm; timer/config fields (_timer,_next24hRefreshUtc,PollInterval,Stats24hInterval,TopStockFailuresCount,
  RecentFailuresDisplayCount,_lastStatus,_aiUserIdsCache,_aiUserIdsLoadedAtUtc,AiUserIdsCacheTtl); ctor;
  StartPolling; StopPolling; Dispose; OnTimerTick; GetAiUserIdsAsync; FormatDuration; FormatRelative. Keeps base+interface.
- **`.LiveStatus.cs`**: the 23 status [ObservableProperty] (_isRunning.._failuresByStockText, incl _scalerEnabled);
  BotCapDisplay; RefreshAsync; BuildRecentFailuresText; BuildFailureBreakdownTexts.
- **`.Panels.cs`**: 24h-stats (_last24hTrades,_last24hVolume,_last24hActiveBots,_last24hVolumeText; Refresh24hStatsAsync)
  + activity-chart (ActivityBucketCount,ActivityRefreshInterval,_nextActivityRefreshUtc,ActivityRangeMinutes,
  ActivityRangeLabels,_activityRangeIndex,_activityTradesText,_activityVolumeText,_activityActiveText,
  ActivityTradesSeries,ActivityVolumeSeries,ActivityActiveSeries,_seriesIndex,CurrentSeriesCaption,ActivityRefreshed,
  OnActivityRangeIndexChanged,OnSeriesIndexChanged,RefreshActivityAsync) + strategy (StrategyRangeMinutes,
  StrategyRangeLabels,_strategyRangeIndex,TraderStrategies,HouseStrategies,_strategyHeadlineText,_strategyTradesHeader,
  _strategyShowVolume,_strategyRangeCapped,StrategyRefreshInterval,_nextStrategyRefreshUtc,OnStrategyRangeIndexChanged,
  RefreshStrategyBreakdownAsync,ReplaceAll).
- **`.Controls.cs`**: export-status fields (_exportFailuresStatusText,_exportLedgerStatusText,_exportEconomyStatusText,
  _exportSentimentStatusText); export/failures cmds (ExportFailuresAsync,ClearFailuresAsync,ExportEconomyAsync,
  ExportSentimentAsync,ExportLedgerAsync,DownloadServerCsvAsync,PickFailureExportPathAsync[#if WINDOWS]); bot-control
  cmds (StartBotsAsync,StopBotsAsync,ApplyMaxBotCapAsync,ApplyMinBotCapAsync); OnScalerEnabledChanged.

Est: spine ~150 · LiveStatus ~185 · Panels ~250 · Controls ~185 — all under 500.

## Gate
Build client csproj green + FULL suite 661/661 + moves-only sorted-line diff (spine pure-deletion + `partial`
already present so class-decl UNCHANGED; each partial's members == removed lines). No XAML/csproj change.
(Pure partial split is XAML-transparent → build + moves-only diff sufficient; no eyeball needed, per arc-1 precedent.)
