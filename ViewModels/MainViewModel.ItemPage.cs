using System.Collections.ObjectModel;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// The limited's item page (Views/ItemPageView), shared by Trades and the Marketplace: its numbers and graph, and the
/// people reselling it with a Buy each (the same confirm card as Deals).
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<MarketSeller> LimitedSellers { get; } = new();
    [ObservableProperty] private bool limitedSellersLoading;
    [ObservableProperty] private bool limitedSellersHasMore;
    [ObservableProperty] private string limitedSellersNote = "";
    private string? _limitedSellersNext;
    private MarketItem? _limitedMarketItem;
    /// <summary>Opened from a "buy" (a watchlist hit, say): the cheapest copy's confirm card opens once the sellers are in.</summary>
    private long _limitedAutoBuy;

    /// <summary>The item page is showing on the Marketplace (a limited opened there).</summary>
    public bool IsMarketLimitedOpen => OpenMarketItem is { IsLimited: true };
    /// <summary>The Marketplace's own item page (everything that isn't a limited).</summary>
    public bool IsMarketPlainItemOpen => IsMarketItemOpen && !IsMarketLimitedOpen;

    public string LimitedBuyText => LimitedSellers.Where(s => !s.IsYours).OrderBy(s => s.Price).FirstOrDefault() is { } c
        ? $"Buy for R$ {c.Price:N0}" : LimitedSellersLoading ? "Loading sellers…" : "Nobody sells it";
    public bool CanBuyLimited => !LimitedSellersLoading && LimitedSellers.Any(s => !s.IsYours) && CurrentAccount != null;

    private void NotifyLimitedSellers()
    {
        OnPropertyChanged(nameof(LimitedBuyText));
        OnPropertyChanged(nameof(CanBuyLimited));
    }

    /// <summary>After the stats are in (they bring the marketplace ID): who resells it, cheapest first.</summary>
    private async Task LoadLimitedSellersAsync(LimitedTile t, string? cursor)
    {
        if (cursor == null) { LimitedSellers.Clear(); _limitedSellersNext = null; LimitedSellersHasMore = false; }
        LimitedSellersNote = "";
        LimitedSellersLoading = true;
        NotifyLimitedSellers();
        try
        {
            var cid = RobloxTrades.CollectibleIdOf(t.AssetId, t.IsBundle);
            if (cid.Length == 0) { LimitedSellersNote = "Roblox doesn't list sellers for this item."; return; }
            _limitedMarketItem = new MarketItem { Id = t.AssetId, Name = t.Name, ItemType = t.IsBundle ? "Bundle" : "Asset", CollectibleItemId = cid, IsLimited = true };
            var (list, next) = await RobloxMarket.GetResellersAsync(cid, cursor, CancellationToken.None);
            if (OpenLimited != t) return;
            var mine = Accounts.Select(a => a.UserId).ToHashSet();
            foreach (var s in list)
            {
                s.IsYours = s.SellerType == "User" && mine.Contains(s.SellerId);
                LimitedSellers.Add(s);
            }
            _limitedSellersNext = next;
            LimitedSellersHasMore = next != null;
            if (LimitedSellers.Count == 0) LimitedSellersNote = "Nobody is reselling this right now.";
            if (_limitedAutoBuy == t.AssetId && cursor == null)
            {
                _limitedAutoBuy = 0;
                if (LimitedSellers.Where(s => !s.IsYours).OrderBy(s => s.Price).FirstOrDefault() is { } cheapest) BuyLimitedSeller(cheapest);
            }
        }
        catch (RobloxException ex)
        {
            if (OpenLimited == t) LimitedSellersNote = ex.TooMany ? "Roblox is busy. Try again in a moment." : ex.Message;
        }
        catch (System.Text.Json.JsonException) { if (OpenLimited == t) LimitedSellersNote = "Roblox sent something unexpected."; }
        finally
        {
            if (OpenLimited == t) LimitedSellersLoading = false;
            NotifyLimitedSellers();
        }
    }

    [RelayCommand]
    private Task LoadMoreLimitedSellers() => OpenLimited is { } t && _limitedSellersNext != null && !LimitedSellersLoading
        ? LoadLimitedSellersAsync(t, _limitedSellersNext) : Task.CompletedTask;

    [RelayCommand]
    private Task ReloadLimitedSellers() => OpenLimited is { } t ? LoadLimitedSellersAsync(t, null) : Task.CompletedTask;

    /// <summary>A seller's Buy: the confirm card with that copy.</summary>
    [RelayCommand]
    private void BuyLimitedSeller(MarketSeller? s)
    {
        if (s == null || s.IsYours || OpenLimited is not { } t || _limitedMarketItem is not { } item || DealBuyBusy) return;
        if (CurrentAccount == null) { StatusMessage = "Pick an account top right first."; return; }
        var info = OpenStats;
        DealBuyDeal = new DealTile
        {
            AssetId = t.AssetId, Name = t.Name, Price = s.Price, Rap = info?.Rap ?? t.Rap, Value = info?.Value ?? t.Value,
            Demand = info?.Demand ?? -1, Projected = t.Projected, ThumbUrl = OpenLimitedPicture
        };
        _dealBuyItem = item;
        _dealBuySeller = s;
        DealBuyFromDeals = false;
        DealBuyError = "";
        DealBuyLoading = false;
        ShowDealBuy = true;
        NotifyDealBuy();
    }

    /// <summary>The big Buy on the item page: the cheapest copy.</summary>
    [RelayCommand]
    private void BuyCheapestLimited()
    {
        if (LimitedSellers.Where(s => !s.IsYours).OrderBy(s => s.Price).FirstOrDefault() is { } c) BuyLimitedSeller(c);
    }
}
