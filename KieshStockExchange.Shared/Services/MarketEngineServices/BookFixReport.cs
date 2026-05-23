namespace KieshStockExchange.Services.MarketEngineServices;

public sealed record BookFixReport(
    int RemovedEmptyPriceLevelsBuys, int RemovedEmptyPriceLevelsSells,
    int RemovedOrphanedOrdersBuys, int RemovedOrphanedOrdersSells,
    int RemovedInvalidOrdersBuys, int RemovedInvalidOrdersSells,
    int RemovedNonOpenLimitBuys, int RemovedNonOpenLimitSells,
    int FixedIndexMismatchesBuys, int FixedIndexMismatchesSells
);
