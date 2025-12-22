using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.PortfolioServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed class OrderExecutionService : IOrderExecutionService
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IOrderBookCache _books;
    private readonly IMatchingEngine _matching;
    private readonly IOrderValidator _validator;
    private readonly ISettlementEngine _settlement;
    private readonly IMarketDataService _marketData;
    private readonly ILogger<OrderExecutionService> _logger;

    public OrderExecutionService(IDataBaseService db, IOrderBookCache books,
        IMatchingEngine matching, IOrderValidator validator, ISettlementEngine settlement,
        IMarketDataService marketData, ILogger<OrderExecutionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _books = books ?? throw new ArgumentNullException(nameof(books));
        _matching = matching ?? throw new ArgumentNullException(nameof(matching));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Order Execution and Matching
    public async Task<OrderResult> PlaceAndMatchAsync(Order incoming, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Validate incoming order
        var validationError = _validator.ValidateNew(incoming);
        if (validationError != null) return validationError;

        // Reserve assets and persist order
        var reserveError = await _settlement.SettleOrderAsync(incoming, ct).ConfigureAwait(false);
        if (reserveError != null) return reserveError;

        // Matching & settlement under book lock
        List<Transaction> trades = new();

        await _books.WithBookLockAsync(incoming.StockId, incoming.CurrencyType, ct, async book =>
        {
            // Run matching against in-memory book
            var matches = await _matching.MatchAsync(incoming, book, ct).ConfigureAwait(false);

            // Persist + apply mutations for each match
            foreach (var match in matches)
            {
                var trade = await _settlement.SettleTradeAsync(match, ct).ConfigureAwait(false);
                trades.Add(trade);
            }

            // If incoming still open and should rest on the book -> insert
            if (incoming.IsOpenLimitOrder)
                book.UpsertOrder(incoming);
            // If incoming still open but cannot rest on book -> cancel remainder
            else if (incoming.IsOpen)
                await _settlement.CancelRemainderAsync(incoming, ct).ConfigureAwait(false);

        }).ConfigureAwait(false);

        // Publish ticks to market data (outside lock)
        foreach (var tick in trades)
            await _marketData.OnTick(tick).ConfigureAwait(false);

        return OrderResultFactory.Success(incoming, trades);
    }

    public async Task<OrderResult> CancelOrderAsync(int orderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Fetch existing order
        Order? order = await _db.GetOrderById(orderId, ct);
        if (order == null) return OrderResultFactory.InvalidParams("Order not found.");

        // Validate cancellation
        var validation = _validator.ValidateCancel(order);
        if (validation != null) return validation;

        // Cancellation under book lock
        await _books.WithBookLockAsync(order!.StockId, order.CurrencyType, ct, async book =>
        {
            // Remove from book if it exists there
            book.RemoveById(order.OrderId); // you may need to add this method

            await _settlement.CancelRemainderAsync(order, ct);
        });

        return OrderResultFactory.Cancelled(order);
    }

    public async Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Fetch existing order
        Order? order = await _db.GetOrderById(orderId, ct);
        if (order == null) return OrderResultFactory.InvalidParams("Order not found.");

        // Validate modification
        var validation = _validator.ValidateModify(order, newQuantity, newPrice);
        if (validation != null) return validation;

        // Modification and re-matching under book lock
        List<Transaction> txs = new();

        await _books.WithBookLockAsync(order.StockId, order.CurrencyType, ct, async book =>
        {
            book.RemoveById(order.OrderId);

            await _settlement.ApplyOrderChangeAsync(order, newQuantity, newPrice, ct);

            // Try matching immediately again after modification
            var matches = await _matching.MatchAsync(order, book, ct);
            foreach (var match in matches)
                txs.Add(await _settlement.SettleTradeAsync(match, ct));

            if (order.IsOpen && order.IsLimitOrder)
                book.UpsertOrder(order);
            else if (order.IsOpen)
                await _settlement.CancelRemainderAsync(order, ct);
        });

        // Publish ticks to market data (outside lock)
        foreach (var t in txs)
            await _marketData.OnTick(t, ct);

        return OrderResultFactory.Modified(order, txs);
    }

    #endregion
}
