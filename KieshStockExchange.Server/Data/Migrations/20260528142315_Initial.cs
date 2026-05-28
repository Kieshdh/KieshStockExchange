using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KieshStockExchange.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AIUsers",
                columns: table => new
                {
                    AiUserId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    DecisionIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TradeProb = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    UseMarketProb = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    UseSlippageMarketProb = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    BuyBiasPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    MinTradeAmountPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    MaxTradeAmountPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    PerPositionMaxPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    MinCashReservePrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    MaxCashReservePrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    SlippageTolerancePrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    MinLimitOffsetPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    MaxLimitOffsetPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    AggressivenessPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    ExtremeReactionRandomnessPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    CashInjectionFrequencyPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    CashInjectionAmountPrc = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    WatchlistCsv = table.Column<string>(type: "text", nullable: false),
                    MinOpenPositions = table.Column<int>(type: "integer", nullable: false),
                    MaxOpenPositions = table.Column<int>(type: "integer", nullable: false),
                    MaxDailyTrades = table.Column<int>(type: "integer", nullable: false),
                    MaxOpenOrders = table.Column<int>(type: "integer", nullable: false),
                    HomeCurrency = table.Column<string>(type: "text", nullable: false),
                    Strategy = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIUsers", x => x.AiUserId);
                });

            migrationBuilder.CreateTable(
                name: "Candles",
                columns: table => new
                {
                    CandleId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    BucketSeconds = table.Column<int>(type: "integer", nullable: false),
                    OpenTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    High = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    Low = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    Close = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: false),
                    TradeCount = table.Column<int>(type: "integer", nullable: false),
                    MinTransactionId = table.Column<int>(type: "integer", nullable: true),
                    MaxTransactionId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candles", x => x.CandleId);
                });

            migrationBuilder.CreateTable(
                name: "Funds",
                columns: table => new
                {
                    FundId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    TotalBalance = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    ReservedBalance = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Funds", x => x.FundId);
                    table.CheckConstraint("CK_Funds_Balance_Invariants", "\"TotalBalance\" >= 0 AND \"ReservedBalance\" >= 0 AND \"ReservedBalance\" <= \"TotalBalance\"");
                });

            migrationBuilder.CreateTable(
                name: "FundTransactions",
                columns: table => new
                {
                    FundTransactionId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FundTransactions", x => x.FundTransactionId);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    MessageId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    OrderId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    StockId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    SlippagePercent = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    BuyBudget = table.Column<decimal>(type: "numeric(20,10)", nullable: true),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    OrderType = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AmountFilled = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    PositionId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    StockId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    ReservedQuantity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.PositionId);
                    table.CheckConstraint("CK_Positions_Quantity_Invariants", "\"Quantity\" >= 0 AND \"ReservedQuantity\" >= 0 AND \"ReservedQuantity\" <= \"Quantity\"");
                });

            migrationBuilder.CreateTable(
                name: "StockListings",
                columns: table => new
                {
                    ListingId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    SeedPrice = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockListings", x => x.ListingId);
                });

            migrationBuilder.CreateTable(
                name: "StockPrices",
                columns: table => new
                {
                    PriceId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockId = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockPrices", x => x.PriceId);
                });

            migrationBuilder.CreateTable(
                name: "Stocks",
                columns: table => new
                {
                    StockId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    CompanyName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Stocks", x => x.StockId);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    TransactionId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockId = table.Column<int>(type: "integer", nullable: false),
                    BuyOrderId = table.Column<int>(type: "integer", nullable: false),
                    SellOrderId = table.Column<int>(type: "integer", nullable: false),
                    BuyerId = table.Column<int>(type: "integer", nullable: false),
                    SellerId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(20,10)", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    BaseCurrency = table.Column<string>(type: "text", nullable: false),
                    ThemeKey = table.Column<string>(type: "text", nullable: false),
                    DefaultCandleResolutionSeconds = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BirthDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "UserWatchlist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    StockId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWatchlist", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AIUsers_Strategy",
                table: "AIUsers",
                column: "Strategy");

            migrationBuilder.CreateIndex(
                name: "IX_UserAi",
                table: "AIUsers",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Candle_Key",
                table: "Candles",
                columns: new[] { "StockId", "Currency", "BucketSeconds", "OpenTime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Funds_User_Currency",
                table: "Funds",
                columns: new[] { "UserId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FundTx_User_Time",
                table: "FundTransactions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Created",
                table: "Messages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_User_Read",
                table: "Messages",
                columns: new[] { "UserId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Stock_Status",
                table: "Orders",
                columns: new[] { "StockId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_User_Status",
                table: "Orders",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_User_Stock",
                table: "Positions",
                columns: new[] { "UserId", "StockId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockListing",
                table: "StockListings",
                columns: new[] { "StockId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockPrices_Stock_Curr_Time",
                table: "StockPrices",
                columns: new[] { "StockId", "Currency", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Stocks_Symbol",
                table: "Stocks",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_BuyerId",
                table: "Transactions",
                column: "BuyerId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_SellerId",
                table: "Transactions",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_Tx_Stock_Curr_Time",
                table: "Transactions",
                columns: new[] { "StockId", "Currency", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserWatchlist_User_Stock",
                table: "UserWatchlist",
                columns: new[] { "UserId", "StockId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AIUsers");

            migrationBuilder.DropTable(
                name: "Candles");

            migrationBuilder.DropTable(
                name: "Funds");

            migrationBuilder.DropTable(
                name: "FundTransactions");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "StockListings");

            migrationBuilder.DropTable(
                name: "StockPrices");

            migrationBuilder.DropTable(
                name: "Stocks");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "UserWatchlist");
        }
    }
}
