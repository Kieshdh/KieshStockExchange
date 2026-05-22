using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// One controller for the six engine multi-write bundles. Each action opens a single
// RunInTransactionAsync over the payload's writes — replaces the client-side pattern
// of BeginTransactionAsync + multiple writes + tx.CommitAsync that HTTP can't carry.
[ApiController]
[Route("api")]
public sealed class EngineController : ControllerBase
{
    private readonly IDataBaseService _db;
    private readonly ILogger<EngineController> _logger;

    public EngineController(IDataBaseService db, ILogger<EngineController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Bundle 1
    [HttpPost("engine/settle-single-order")]
    public async Task<ActionResult<SettleSingleOrderResult>> SettleSingleOrder(
        [FromBody] SettleSingleOrderCommand cmd, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async _ =>
        {
            await _db.CreateOrder(cmd.Order, ct).ConfigureAwait(false);
            if (cmd.BuyFund is not null)
                await _db.UpdateAllAsync(new[] { cmd.BuyFund }, ct).ConfigureAwait(false);
            if (cmd.SellPosition is not null)
                await _db.UpdateAllAsync(new[] { cmd.SellPosition }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return Ok(new SettleSingleOrderResult(cmd.Order));
    }

    // Bundle 2
    [HttpPost("engine/place-orders-batch")]
    public async Task<ActionResult<PlaceOrdersBatchResult>> PlaceOrdersBatch(
        [FromBody] PlaceOrdersBatchCommand cmd, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async _ =>
        {
            await _db.InsertAllAsync(cmd.Orders, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return Ok(new PlaceOrdersBatchResult(cmd.Orders));
    }

    // Bundle 3
    [HttpPost("engine/settle-trade-group")]
    public async Task<ActionResult<SettleTradeGroupResult>> SettleTradeGroup(
        [FromBody] SettleTradeGroupCommand cmd, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async _ =>
        {
            if (cmd.AcceptedTrades.Count > 0)
                await _db.InsertAllAsync(cmd.AcceptedTrades, ct).ConfigureAwait(false);
            if (cmd.OrdersToUpdate.Count > 0)
                await _db.UpdateAllAsync(cmd.OrdersToUpdate, ct).ConfigureAwait(false);
            if (cmd.FundsToUpdate.Count > 0)
                await _db.UpdateAllAsync(cmd.FundsToUpdate, ct).ConfigureAwait(false);
            if (cmd.PositionsToUpdate.Count > 0)
                await _db.UpdateAllAsync(cmd.PositionsToUpdate, ct).ConfigureAwait(false);
            if (cmd.NewPositions.Count > 0)
                await _db.InsertAllAsync(cmd.NewPositions, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return Ok(new SettleTradeGroupResult(cmd.AcceptedTrades, cmd.NewPositions));
    }

    // Bundle 4
    [HttpPost("engine/apply-order-change")]
    public async Task<IActionResult> ApplyOrderChange(
        [FromBody] ApplyOrderChangeCommand cmd, CancellationToken ct)
    {
        await _db.RunInTransactionAsync(async _ =>
        {
            // Defensive re-fetch: confirms the order still exists + is Open before we
            // overwrite it. Mirrors the check the in-process OrderModifier did.
            var existing = await _db.GetOrderById(cmd.Order.OrderId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Order #{cmd.Order.OrderId} not found.");
            if (!existing.IsOpen)
                throw new InvalidOperationException("Only open orders can be modified.");

            await _db.UpdateOrder(cmd.Order, ct).ConfigureAwait(false);
            if (cmd.SellPosition is not null)
                await _db.UpdateAllAsync(new[] { cmd.SellPosition }, ct).ConfigureAwait(false);
            if (cmd.BuyFund is not null)
                await _db.UpdateAllAsync(new[] { cmd.BuyFund }, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return NoContent();
    }

    // Bundle 5
    [HttpPost("portfolio/deposit-withdraw")]
    public async Task<ActionResult<bool>> DepositWithdraw(
        [FromBody] DepositWithdrawCommand cmd, CancellationToken ct)
    {
        if (cmd.Amount <= 0)
        {
            _logger.LogWarning("DepositWithdraw: amount must be positive. Given {Amount}", cmd.Amount);
            return Ok(false);
        }
        if (!CurrencyHelper.IsSupported(cmd.Currency))
        {
            _logger.LogWarning("DepositWithdraw: unsupported currency {Currency}", cmd.Currency);
            return Ok(false);
        }
        if (cmd.Kind is not (FundTransaction.Kinds.Deposit or FundTransaction.Kinds.Withdrawal))
        {
            _logger.LogWarning("DepositWithdraw: unknown kind {Kind}", cmd.Kind);
            return Ok(false);
        }

        try
        {
            await _db.RunInTransactionAsync(async _ =>
            {
                var fund = await _db.GetFundByUserIdAndCurrency(cmd.UserId, cmd.Currency, ct).ConfigureAwait(false)
                    ?? new Fund { UserId = cmd.UserId, CurrencyType = cmd.Currency, TotalBalance = 0 };

                if (cmd.Kind == FundTransaction.Kinds.Deposit)
                {
                    fund.AddFunds(cmd.Amount);
                }
                else
                {
                    if (!CurrencyHelper.GreaterOrEqual(fund.AvailableBalance, cmd.Amount, cmd.Currency))
                        throw new InsufficientFundsException();
                    fund.WithdrawFunds(cmd.Amount);
                }

                await _db.UpsertFund(fund, ct).ConfigureAwait(false);
                await _db.CreateFundTransaction(new FundTransaction
                {
                    UserId = cmd.UserId,
                    CurrencyType = cmd.Currency,
                    Amount = cmd.Amount,
                    Kind = cmd.Kind,
                    Note = string.IsNullOrWhiteSpace(cmd.Note) ? null : cmd.Note.Trim(),
                    CreatedAt = TimeHelper.NowUtc()
                }, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return Ok(true);
        }
        catch (InsufficientFundsException) { return Ok(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DepositWithdraw failed for user {UserId} ({Kind} {Amount} {Currency})",
                cmd.UserId, cmd.Kind, cmd.Amount, cmd.Currency);
            return Ok(false);
        }
    }

    // Bundle 6
    [HttpPost("portfolio/convert-internal")]
    public async Task<ActionResult<bool>> ConvertInternal(
        [FromBody] ConvertInternalCommand cmd, CancellationToken ct)
    {
        if (cmd.Amount <= 0 || cmd.ConvertedAmount <= 0)
        {
            _logger.LogWarning("ConvertInternal: amount or converted amount not positive ({A}/{B})",
                cmd.Amount, cmd.ConvertedAmount);
            return Ok(false);
        }
        if (cmd.FromCurrency == cmd.ToCurrency)
        {
            _logger.LogWarning("ConvertInternal: from/to must differ ({Currency})", cmd.FromCurrency);
            return Ok(false);
        }
        if (!CurrencyHelper.IsSupported(cmd.FromCurrency) || !CurrencyHelper.IsSupported(cmd.ToCurrency))
        {
            _logger.LogWarning("ConvertInternal: unsupported currency {From}->{To}", cmd.FromCurrency, cmd.ToCurrency);
            return Ok(false);
        }

        try
        {
            await _db.RunInTransactionAsync(async _ =>
            {
                var src = await _db.GetFundByUserIdAndCurrency(cmd.UserId, cmd.FromCurrency, ct).ConfigureAwait(false);
                if (src is null || !CurrencyHelper.GreaterOrEqual(src.AvailableBalance, cmd.Amount, cmd.FromCurrency))
                    throw new InsufficientFundsException();

                var dst = await _db.GetFundByUserIdAndCurrency(cmd.UserId, cmd.ToCurrency, ct).ConfigureAwait(false)
                    ?? new Fund { UserId = cmd.UserId, CurrencyType = cmd.ToCurrency, TotalBalance = 0 };

                src.WithdrawFunds(cmd.Amount);
                dst.AddFunds(cmd.ConvertedAmount);

                await _db.UpsertFund(src, ct).ConfigureAwait(false);
                await _db.UpsertFund(dst, ct).ConfigureAwait(false);

                var now = TimeHelper.NowUtc();
                await _db.CreateFundTransaction(new FundTransaction
                {
                    UserId = cmd.UserId,
                    CurrencyType = cmd.FromCurrency,
                    Amount = cmd.Amount,
                    Kind = FundTransaction.Kinds.ConversionOut,
                    Note = cmd.OutNote,
                    CreatedAt = now
                }, ct).ConfigureAwait(false);
                await _db.CreateFundTransaction(new FundTransaction
                {
                    UserId = cmd.UserId,
                    CurrencyType = cmd.ToCurrency,
                    Amount = cmd.ConvertedAmount,
                    Kind = FundTransaction.Kinds.ConversionIn,
                    Note = cmd.InNote,
                    CreatedAt = now
                }, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return Ok(true);
        }
        catch (InsufficientFundsException) { return Ok(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertInternal failed for user {UserId} ({A} {From} -> {B} {To})",
                cmd.UserId, cmd.Amount, cmd.FromCurrency, cmd.ConvertedAmount, cmd.ToCurrency);
            return Ok(false);
        }
    }

    // Sentinel for early-exit-via-rollback inside RunInTransactionAsync.
    private sealed class InsufficientFundsException : Exception { }
}
