using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Facade forwarding ISettlementEngine to four operation helpers. </summary>
public sealed class SettlementEngine : ISettlementEngine
{
    #region Services and Constructor
    private readonly OrderSettler _orderSettler;
    private readonly OrderCanceller _orderCanceller;
    private readonly OrderModifier _orderModifier;
    private readonly StopModifier _stopModifier;
    private readonly TradeSettler _tradeSettler;

    public SettlementEngine(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, IOrderRegistry registry, ILogger<SettlementEngine> logger,
        ILoggerFactory loggerFactory, IOptions<SeparatorLoggerOptions> loggerOptions)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));
        if (accounts is null) throw new ArgumentNullException(nameof(accounts));
        if (ledger is null) throw new ArgumentNullException(nameof(ledger));
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        if (loggerFactory is null) throw new ArgumentNullException(nameof(loggerFactory));
        if (loggerOptions is null) throw new ArgumentNullException(nameof(loggerOptions));

        // Construct SeparatorLogger<T> directly — loggerFactory.CreateLogger<T>() bypasses
        // the open-generic DI mapping at MauiProgram.cs and returns an unwrapped logger
        ILogger<T> SepLogger<T>() => new SeparatorLogger<T>(loggerFactory, loggerOptions);

        var validator = new SellerCapacityValidator(SepLogger<SellerCapacityValidator>());
        var probe     = new ConservationProbe      (SepLogger<ConservationProbe>());

        _orderSettler   = new OrderSettler  (db, accounts, ledger, registry, SepLogger<OrderSettler>());
        _orderCanceller = new OrderCanceller(db, accounts, ledger, registry, SepLogger<OrderCanceller>());
        _orderModifier  = new OrderModifier (db, accounts, ledger, SepLogger<OrderModifier>());
        _stopModifier   = new StopModifier  (db, accounts, ledger, SepLogger<StopModifier>());
        _tradeSettler   = new TradeSettler  (db, accounts, ledger, SepLogger<TradeSettler>(),
                                             validator, probe);
    }
    #endregion

    #region ISettlementEngine forwards
    public Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default)
        => _orderSettler.SettleAsync(incoming, ct);

    public Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesAsync(
        IReadOnlyList<Transaction> trades, Dictionary<int, Order> ordersById,
        CancellationToken ct = default)
        => _tradeSettler.SettleAsync(trades, ordersById, ct);

    public Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        TradeBatchScope scope,
        CancellationToken ct = default)
        => _tradeSettler.SettleNoTxAsync(trades, ordersById, scope, ct);

    public void RestoreCacheSnapshots(Dictionary<int, Order> ordersById, TradeBatchScope scope)
        => _tradeSettler.RestoreSnapshots(ordersById, scope);

    public Task CancelRemainderAsync(Order order, CancellationToken ct = default, bool callerHoldsGate = false)
        => _orderCanceller.CancelAsync(order, ct, callerHoldsGate);

    public Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default)
        => _orderModifier.ApplyChangeAsync(order, newQuantity, newPrice, ct);

    public Task ApplyStopChangeAsync(Order order, int? newQuantity, decimal? newStopPrice,
        decimal? newLimitPrice, CancellationToken ct = default)
        => _stopModifier.ApplyChangeAsync(order, newQuantity, newStopPrice, newLimitPrice, ct);
    #endregion
}
