using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.CommandDtos;

// Bundle 1 — OrderSettler.SettleAsync
// Engine has already reserved cash/shares in memory; the bundle persists the order
// row + the touched fund or position in one server-side tx and returns the OrderId.
public sealed record SettleSingleOrderCommand(
    Order Order,
    Fund? BuyFund,
    Position? SellPosition);

public sealed record SettleSingleOrderResult(Order Order);

// Bundle 2 — OrderExecutionService.PlaceAndMatchBatchAsync Phase 2
// Single InsertAll under a root tx; server returns assigned PKs in payload order so
// the engine can update its in-memory canonical references before matching runs.
public sealed record PlaceOrdersBatchCommand(List<Order> Orders);
public sealed record PlaceOrdersBatchResult(List<Order> Orders);

// Bundle 3 — OrderExecutionService.RunGroupTxAsync Phase 3 / TradeSettler.SettleNoTxAsync
// The matcher has already mutated orders/funds/positions in memory; the bundle just
// persists the final state. NewPositions get auto-assigned PKs; the accepted-trades
// list returns with TransactionIds populated.
public sealed record SettleTradeGroupCommand(
    List<Transaction> AcceptedTrades,
    List<Order> OrdersToUpdate,
    List<Fund> FundsToUpdate,
    List<Position> PositionsToUpdate,
    List<Position> NewPositions);

public sealed record SettleTradeGroupResult(
    List<Transaction> AcceptedTrades,   // with assigned TransactionIds
    List<Position> NewPositions);       // with assigned PositionIds

// Bundle 4 — OrderModifier.ApplyChangeAsync
// Order carries the post-update state (UpdatePrice / UpdateQuantity already applied
// by the engine). BuyFund / SellPosition carry the post-delta cache state.
public sealed record ApplyOrderChangeCommand(
    Order Order,
    Fund? BuyFund,
    Position? SellPosition);

// Bundle 5 — UserPortfolioService.DepositOrWithdrawAsync
// Server runs the existing balance check + Fund upsert + FundTransaction audit row
// inside one tx. Returns false on insufficient funds (withdrawal) or any other
// validation failure; the controller swallows InsufficientFundsException -> false.
public sealed record DepositWithdrawCommand(
    int UserId,
    CurrencyType Currency,
    decimal Amount,
    string Kind,         // FundTransaction.Kinds.Deposit | Withdrawal
    string? Note);

// Bundle 6 — UserPortfolioService.ConvertInternalAsync
// FxRateService is still client-side, so the client passes the bid-rate-derived
// ConvertedAmount along with the source Amount. Server doesn't re-quote.
// OutNote and InNote already carry the "Convert X->Y @ rate" tag.
public sealed record ConvertInternalCommand(
    int UserId,
    CurrencyType FromCurrency,
    CurrencyType ToCurrency,
    decimal Amount,
    decimal ConvertedAmount,
    string? OutNote,
    string? InNote);
