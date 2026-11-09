using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// Buying from Trades → Deals without leaving the tab: a confirm card with the cheapest copy's real price, or, with
/// insta-buy on, straight away (only at the deal's price or less, and never over the spending limit).
/// </summary>
public partial class MainViewModel
{
    // ------------------------------------------------------------------ insta-buy

    private bool _dealInstaBuy;

    /// <summary>
    /// The insta-buy switch. Turning it on only asks first (the red warning); it's on once that's confirmed.
    /// Not saved: every start of RoDeck begins with it off.
    /// </summary>
    public bool DealInstaBuy
    {
        get => _dealInstaBuy;
        set
        {
            if (value == _dealInstaBuy) return;
            if (value)
            {
                ShowInstaBuyWarning = true;
                // the switch flips back until the warning is confirmed
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(DealInstaBuy)));
                return;
            }
            _dealInstaBuy = false;
            OnPropertyChanged();
            StatusMessage = "Insta-buy is off. Buy asks first again.";
        }
    }

    [ObservableProperty] private bool showInstaBuyWarning;

    [RelayCommand]
    private void ConfirmInstaBuy()
    {
        ShowInstaBuyWarning = false;
        _dealInstaBuy = true;
        OnPropertyChanged(nameof(DealInstaBuy));
        StatusMessage = "Insta-buy is on: Buy on a deal buys the cheapest copy at once.";
        Log(ActivityLog.Info, "Insta-buy turned on for Deals.");
    }

    [RelayCommand] private void CancelInstaBuy() => ShowInstaBuyWarning = false;

    // ------------------------------------------------------------------ the confirm card on Deals

    [ObservableProperty] private bool showDealBuy;
    [ObservableProperty] private DealTile? dealBuyDeal;
    [ObservableProperty] private bool dealBuyLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirmDealBuy))] private bool dealBuyBusy;
    [ObservableProperty] private string dealBuyError = "";
    /// <summary>The card is for a deal (it offers View item); false for a seller on the item page.</summary>
    [ObservableProperty] private bool dealBuyFromDeals;
    private MarketItem? _dealBuyItem;
    private MarketSeller? _dealBuySeller;

    public long DealBuyPrice => _dealBuySeller?.Price ?? 0;
    public string DealBuyPriceText => _dealBuySeller is { } s ? $"R$ {s.Price:N0}" : DealBuyLoading ? "Checking…" : "–";
    public string DealBuySellerText => _dealBuySeller is { } s ? s.Name + (s.Serial > 0 ? $"  ·  copy #{s.Serial:N0}" : "") : "";
    public string DealBuyAfterText => CurrentAccount?.Robux is long r && _dealBuySeller is { } s
        ? $"R$ {r:N0}  →  R$ {Math.Max(0, r - s.Price):N0}" : CurrentAccount?.Robux is long r2 ? $"R$ {r2:N0}" : "Unknown";
    /// <summary>The price went up since the deal was listed (someone bought that copy).</summary>
    public string DealBuyChangedText => _dealBuySeller is { } s && DealBuyDeal is { } d && s.Price > d.Price
        ? $"The cheapest copy is R$ {s.Price:N0} now, not R$ {d.Price:N0}: the deal was bought by someone else." : "";
    public string ConfirmDealBuyText => DealBuyBusy ? "Buying…" : _dealBuySeller is { } s ? $"Buy for R$ {s.Price:N0}" : "Buy";
    public bool CanConfirmDealBuy => !DealBuyBusy && !DealBuyLoading && _dealBuySeller != null && _dealBuyItem != null
        && CurrentAccount != null && DealBuyProblem(_dealBuySeller.Price).Length == 0;

    private string DealBuyProblem(long price) =>
        CurrentAccount?.Robux is long r && r < price ? "This account doesn't have enough Robux."
        : Settings.MarketSpendLimit > 0 && price > Settings.MarketSpendLimit
            ? $"That's over your spending limit of R$ {Settings.MarketSpendLimit:N0} (Settings → Marketplace & trades)."
        : "";

    private void NotifyDealBuy()
    {
        foreach (var n in new[] { nameof(DealBuyPrice), nameof(DealBuyPriceText), nameof(DealBuySellerText), nameof(DealBuyAfterText),
                                  nameof(DealBuyChangedText), nameof(ConfirmDealBuyText), nameof(CanConfirmDealBuy) })
            OnPropertyChanged(n);
    }

    /// <summary>A deal's Buy: the confirm card here, or with insta-buy on, the purchase itself.</summary>
    [RelayCommand]
    private async Task BuyDeal(DealTile? d)
    {
        if (d == null) return;
        if (CurrentAccount is not { } a) { StatusMessage = "Pick an account top right first."; return; }
        if (DealBuyBusy) return;

        DealBuyDeal = d;
        DealBuyFromDeals = true;
        DealBuyError = "";
        _dealBuyItem = null;
        _dealBuySeller = null;
        DealBuyLoading = true;
        if (!DealInstaBuy) ShowDealBuy = true;
        NotifyDealBuy();
        try
        {
            var item = await RobloxMarket.GetItemAsync(d.AssetId, false, a.Cookie, CancellationToken.None);
            var (sellers, _) = item.CollectibleItemId.Length > 0
                ? await RobloxMarket.GetResellersAsync(item.CollectibleItemId, null, CancellationToken.None)
                : (new List<MarketSeller>(), null);
            if (DealBuyDeal != d) return;
            var mine = Accounts.Select(x => x.UserId).ToHashSet();
            foreach (var s in sellers) s.IsYours = s.SellerType == "User" && mine.Contains(s.SellerId);
            _dealBuyItem = item;
            _dealBuySeller = sellers.Where(s => !s.IsYours).OrderBy(s => s.Price).FirstOrDefault();
            if (_dealBuySeller == null) { DealBuyError = "That copy was already sold. Nobody is reselling this right now."; }
        }
        catch (RobloxException ex) { DealBuyError = ex.TooMany ? "Roblox is busy. Try again in a moment." : ex.Message; }
        catch (System.Text.Json.JsonException) { DealBuyError = "Roblox sent something unexpected. Try again."; }
        finally
        {
            DealBuyLoading = false;
            NotifyDealBuy();
        }

        if (!DealInstaBuy) return;
        // insta-buy: only at the price the deal showed (or less), within the limit and the Robux there is
        if (DealBuyError.Length > 0) { StatusMessage = $"{d.Name}: {DealBuyError}"; return; }
        if (_dealBuySeller is not { } seller || _dealBuyItem is not { } it) return;
        if (seller.Price > d.Price) { StatusMessage = $"Not bought: {d.Name} costs R$ {seller.Price:N0} now, not R$ {d.Price:N0}."; return; }
        if (DealBuyProblem(seller.Price) is { Length: > 0 } problem) { StatusMessage = "Not bought: " + problem; return; }
        await DoDealBuyAsync(a, it, seller);
    }

    [RelayCommand]
    private async Task ConfirmDealBuy()
    {
        if (!CanConfirmDealBuy || CurrentAccount is not { } a || _dealBuyItem is not { } it || _dealBuySeller is not { } s) return;
        await DoDealBuyAsync(a, it, s);
    }

    [RelayCommand]
    private void CancelDealBuy()
    {
        if (DealBuyBusy) return;
        ShowDealBuy = false;
        DealBuyDeal = null;
    }

    private async Task DoDealBuyAsync(Account a, MarketItem item, MarketSeller seller)
    {
        DealBuyBusy = true;
        DealBuyError = "";
        NotifyDealBuy();
        try
        {
            var result = await RobloxMarket.BuyResaleAsync(a.Cookie, a.UserId, item, seller, CancellationToken.None);
            if (result.Ok)
            {
                ShowDealBuy = false;
                var msg = $"{a.Label} bought {item.Name} from {seller.Name} for R$ {seller.Price:N0}.";
                StatusMessage = msg;
                Log(ActivityLog.Purchase, msg + (DealInstaBuy ? " (insta-buy)" : ""));
                if (Deals.FirstOrDefault(x => x.AssetId == item.Id) is { } gone) Deals.Remove(gone);
                LimitedSellers.Remove(seller);
                NotifyLimitedSellers();
                _avatarLoadedAt = DateTime.MinValue;
                _wardrobeAll = new();
                if (await RobloxApi.GetRobuxAsync(a.Cookie, a.UserId) is long r) a.Robux = r;
            }
            else if (ShowDealBuy) DealBuyError = result.Message;
            else StatusMessage = $"{item.Name} not bought: {result.Message}";
        }
        catch (RobloxException ex)
        {
            var msg = ex.TooMany ? "Roblox is busy. Nothing was bought. Try again in a moment." : ex.Message;
            if (ShowDealBuy) DealBuyError = msg; else StatusMessage = msg;
        }
        finally
        {
            DealBuyBusy = false;
            NotifyDealBuy();
        }
    }

    /// <summary>A deal's picture, name, price or View: its item page in Trades.</summary>
    [RelayCommand]
    private Task ViewDeal(DealTile? d)
    {
        if (d == null) return Task.CompletedTask;
        var t = LimitedTile.From(d.AssetId, d.Name, d.Rap);
        t.ThumbUrl = d.ThumbUrl;
        if (Page != "Trades") Page = "Trades";
        return OpenLimitedFromInventory(t);   // switches to the item page, Back returns to Deals
    }
}
