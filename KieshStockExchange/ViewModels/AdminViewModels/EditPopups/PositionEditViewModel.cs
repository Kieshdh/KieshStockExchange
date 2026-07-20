using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.AdminViewModels.EditPopups;

public partial class PositionEditViewModel : ModalFormViewModel
{
    #region Fields, events and Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<PositionEditViewModel> _logger;

    private Position? _original;

    public event EventHandler? Saved;
    #endregion

    #region Bound state
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _stockSymbol = string.Empty;
    [ObservableProperty] private string _quantityText = "0";
    [ObservableProperty] private string _reservedText = "0";
    #endregion

    public PositionEditViewModel(IDataBaseService db, ILogger<PositionEditViewModel> logger)
    {
        Title = "Edit position";
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Initialize and commands
    public void Initialize(Position position, string stockSymbol)
    {
        _original = position ?? throw new ArgumentNullException(nameof(position));
        UserId = position.UserId;
        StockSymbol = stockSymbol;
        QuantityText = position.Quantity.ToString();
        ReservedText = position.ReservedQuantity.ToString();
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_original is null) return;
        ErrorMessage = string.Empty;

        if (!int.TryParse(QuantityText, out var qty) || qty < 0)
        {
            ErrorMessage = "Quantity must be a non-negative integer.";
            return;
        }
        if (!int.TryParse(ReservedText, out var reserved) || reserved < 0)
        {
            ErrorMessage = "Reserved must be a non-negative integer.";
            return;
        }
        if (reserved > qty)
        {
            ErrorMessage = "Reserved cannot exceed Quantity.";
            return;
        }

        var draft = new Position
        {
            PositionId = _original.PositionId,
            UserId = _original.UserId,
            StockId = _original.StockId,
            CreatedAt = _original.CreatedAt,
            Quantity = qty,
            ReservedQuantity = reserved,
            UpdatedAt = TimeHelper.NowUtc(),
        };

        if (!draft.IsValid())
        {
            ErrorMessage = "Position is invalid (check user/stock ids).";
            return;
        }

        IsBusy = true;
        try
        {
            await _db.UpsertPosition(draft).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PositionEditViewModel: save failed for position #{PositionId}", draft.PositionId);
            ErrorMessage = "Save failed. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Saved?.Invoke(this, EventArgs.Empty);
        RequestClose();
    }
    #endregion

    // Drop the Saved handler ref too; the base clears CloseRequested + guards idempotency.
    protected override void OnDispose() => Saved = null;
}
