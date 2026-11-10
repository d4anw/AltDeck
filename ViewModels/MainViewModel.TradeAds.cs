using System.Collections.ObjectModel;
using System.Windows.Threading;
using RoDeck.Models;
using RoDeck.Services;
using RoDeck.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// Trades → Trade ads: one Rolimons trade ad (4 offer slots; 4 want slots that hold items or tags) that RoDeck posts
/// again every 15+ minutes while it runs, also from the tray. Off until you start it. Clicking a slot opens a picker.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<AdSlot> AdOfferSlots { get; } = new();
    public ObservableCollection<AdSlot> AdWantSlots { get; } = new();
    public ObservableCollection<AdTag> AdTags { get; } = new();
    [ObservableProperty] private string adStatus = "";
    [ObservableProperty] private string adLastResult = "";
    [ObservableProperty] private string roliCookieInput = "";
    [ObservableProperty] private string adOfferValue = "0";
    [ObservableProperty] private string adWantValue = "0";
    public bool HasRoliCookie => Settings.RoliVerification.Length > 0;
    /// <summary>The ad on screen (this account's) is the one being posted.</summary>
    public bool AdRunning => Settings.TradeAdOn && Settings.TradeAdAccountId == _adShownFor;
    /// <summary>An ad is being posted, for any account (the dot on the tab).</summary>
    public bool AdRunningAnywhere => Settings.TradeAdOn;
    private int OfferCount => AdOfferSlots.Count(s => s.Item != null);
    private int WantCount => AdWantSlots.Count(s => !s.IsEmpty);
    public bool CanStartAd => HasRoliCookie && OfferCount > 0 && WantCount > 0;

    private readonly DispatcherTimer _adTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _adPosting, _adSetUp;
    /// <summary>The account whose ad the slots show (always the current account once the tab has been opened).</summary>
    private string _adShownFor = "";
    private DateTime _adRetryAt = DateTime.MinValue;

    /// <summary>Called once at start: the tags, the slots and the timer (which does nothing while the ad is off).</summary>
    private void SetupTradeAds()
    {
        foreach (var (tag, name) in RolimonsAds.Tags) AdTags.Add(new AdTag { Tag = tag, Name = name });
        for (int i = 0; i < 4; i++)
        {
            AdOfferSlots.Add(new AdSlot { Index = i, IsOffer = true });
            AdWantSlots.Add(new AdSlot { Index = i, IsOffer = false });
        }
        _adTimer.Tick += async (_, _) => await Quietly("Trade ad", () => AdTickAsync());
        _adTimer.Start();
    }

    /// <summary>When the Trade ads tab opens: show the current account's ad (with values and pictures) and its limiteds.</summary>
    private void LoadTradeAdTab()
    {
        _adSetUp = true;
        if (_adShownFor != (CurrentAccount?.Id ?? "")) ShowAdFor(CurrentAccount);
        else if (CurrentAccount is { } a && _myTradableFor != a.Id) _ = LoadMyTradableAsync(a, null);
        NotifyAd();
    }

    /// <summary>The account's own ad, or a new empty one. The old single ad goes to the account it was posted for.</summary>
    private TradeAdDraft AdDraftFor(string accountId)
    {
        if (Settings.TradeAds.Count == 0 && (Settings.TradeAdOffer.Count > 0 || Settings.TradeAdRequest.Count > 0 || Settings.TradeAdTags.Count > 0))
        {
            var owner = Settings.TradeAdAccountId.Length > 0 ? Settings.TradeAdAccountId : accountId;
            Settings.TradeAds[owner] = new TradeAdDraft { Offer = Settings.TradeAdOffer, Request = Settings.TradeAdRequest, Tags = Settings.TradeAdTags };
            Settings.TradeAdOffer = new(); Settings.TradeAdRequest = new(); Settings.TradeAdTags = new();
        }
        if (!Settings.TradeAds.TryGetValue(accountId, out var d)) Settings.TradeAds[accountId] = d = new TradeAdDraft();
        return d;
    }

    /// <summary>Fill the slots with this account's ad and load its limiteds for the Offer picker.</summary>
    private void ShowAdFor(Account? a)
    {
        IsAdPickerOpen = false;
        _adShownFor = a?.Id ?? "";
        foreach (var s in AdOfferSlots) s.Clear();
        foreach (var s in AdWantSlots) s.Clear();
        if (a == null) return;
        var d = AdDraftFor(a.Id);
        var tiles = new List<LimitedTile>();
        for (int i = 0; i < 4 && i < d.Offer.Count; i++)
            tiles.Add(AdOfferSlots[i].Item = LimitedTile.From(d.Offer[i], LimitedValues.Get(d.Offer[i])?.Name ?? $"Item {d.Offer[i]}", LimitedValues.Get(d.Offer[i])?.Rap ?? 0));
        int w = 0;
        foreach (var id in d.Request.Take(4)) tiles.Add(AdWantSlots[w++].Item = LimitedTile.From(id, LimitedValues.Get(id)?.Name ?? $"Item {id}", LimitedValues.Get(id)?.Rap ?? 0));
        foreach (var tag in d.Tags)
            if (w < 4 && AdTags.FirstOrDefault(t => t.Tag == tag) is { } t) AdWantSlots[w++].Tag = t;
        _ = FillLimitedThumbsAsync(tiles);
        if (_myTradableFor != a.Id) _ = LoadMyTradableAsync(a, null);
    }

    /// <summary>Account switched: the ad page follows (only once the tab has been opened).</summary>
    private void TradeAdsAccountChanged()
    {
        if (!_adSetUp) return;
        ShowAdFor(CurrentAccount);
        NotifyAd();
    }

    // ================================================================== the picker

    [ObservableProperty] private bool isAdPickerOpen;
    [ObservableProperty] private AdSlot? adPickerSlot;
    [ObservableProperty] private string adPickerSearch = "";
    [ObservableProperty] private bool adPickerAscending;
    [ObservableProperty] private string adPickerMin = "", adPickerMax = "";
    [ObservableProperty] private string adPickerEmptyText = "Nothing matches.";
    public ObservableCollection<LimitedTile> AdPickerItems { get; } = new();
    public bool AdPickerShowsTags => AdPickerSlot is { IsOffer: false };
    public string AdPickerTitle => AdPickerSlot is not { } s ? "" : s.IsOffer ? $"Offer slot {s.Index + 1}: one of your limiteds" : $"Want slot {s.Index + 1}: a tag or any limited";

    partial void OnAdPickerSlotChanged(AdSlot? value) { OnPropertyChanged(nameof(AdPickerShowsTags)); OnPropertyChanged(nameof(AdPickerTitle)); }
    partial void OnAdPickerSearchChanged(string value) => FillAdPicker();
    partial void OnAdPickerAscendingChanged(bool value) { OnPropertyChanged(nameof(AdPickerSortText)); FillAdPicker(); }
    public string AdPickerSortText => AdPickerAscending ? "Lowest first" : "Highest first";
    [RelayCommand] private void ToggleAdPickerSort() => AdPickerAscending = !AdPickerAscending;
    partial void OnAdPickerMinChanged(string value) => FillAdPicker();
    partial void OnAdPickerMaxChanged(string value) => FillAdPicker();

    /// <summary>A filled slot: click empties it. An empty slot: click opens the picker to fill it.</summary>
    [RelayCommand]
    private void OpenAdSlot(AdSlot? slot)
    {
        if (slot == null) return;
        if (!slot.IsEmpty) { slot.Clear(); OnAdChanged(); return; }
        AdPickerSlot = slot;
        AdPickerSearch = "";
        IsAdPickerOpen = true;
        // the limiteds must be this account's: load them if they're missing or another account's
        if (slot.IsOffer && CurrentAccount is { } a && _myTradableFor != a.Id && MyTradableLoad.State != LoadState.Loading)
            _ = LoadMyTradableAsync(a, null);
        FillAdPicker();
    }

    [RelayCommand] private void CloseAdPicker() => IsAdPickerOpen = false;

    [RelayCommand] private void SortAdPicker(string? dir) => AdPickerAscending = dir == "asc";

    private void FillAdPicker()
    {
        if (AdPickerSlot is not { } slot) return;
        var q = (AdPickerSearch ?? "").Trim();
        long? min = Limited.ParseAmount(AdPickerMin), max = Limited.ParseAmount(AdPickerMax);
        IEnumerable<LimitedTile> source = slot.IsOffer
            // one tile per item you own; bundles (like dynamic heads) can't go in a Rolimons ad
            ? _myTradableAll.Where(t => !t.IsBundle).GroupBy(t => t.AssetId).Select(g => g.First())
            : LimitedValues.All.Select(v => LimitedTile.From(v.AssetId, v.Name, v.Rap));
        var list = source.Where(t =>
                (q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(LimitedValues.Get(t.AssetId)?.Acronym, q, StringComparison.OrdinalIgnoreCase))
                && (min == null || t.Worth >= min) && (max == null || t.Worth <= max));
        list = AdPickerAscending ? list.OrderBy(t => t.Worth) : list.OrderByDescending(t => t.Worth);
        var shown = list.Take(120).ToList();
        AdPickerItems.Clear();
        foreach (var t in shown) AdPickerItems.Add(t);
        AdPickerEmptyText = !slot.IsOffer ? "Nothing matches."
            : _myTradableFor != (CurrentAccount?.Id ?? "") ? "Loading your limiteds…"
            : MyTradableLoad.State == LoadState.Error ? "Couldn't load your limiteds. Close this and try again."
            : !_myTradableAll.Any(t => !t.IsBundle) ? "This account has no limiteds it can offer."
            : "Nothing matches.";
        _ = FillLimitedThumbsAsync(shown.Where(t => t.ThumbUrl.Length == 0).ToList());
    }

    [RelayCommand]
    private void PickAdItem(LimitedTile? t)
    {
        if (t == null || AdPickerSlot is not { } slot) return;
        var row = slot.IsOffer ? AdOfferSlots : AdWantSlots;
        foreach (var other in row) if (other != slot && other.Item?.AssetId == t.AssetId) other.Clear();   // no doubles: it moves here
        slot.Tag = null;
        slot.Item = t;
        IsAdPickerOpen = false;
        OnAdChanged();
    }

    [RelayCommand]
    private void PickAdTag(AdTag? tag)
    {
        if (tag == null || AdPickerSlot is not { IsOffer: false } slot) return;
        foreach (var other in AdWantSlots) if (other != slot && other.Tag == tag) other.Clear();
        slot.Item = null;
        slot.Tag = tag;
        IsAdPickerOpen = false;
        OnAdChanged();
    }

    [RelayCommand]
    private void ClearAdSlot()
    {
        AdPickerSlot?.Clear();
        IsAdPickerOpen = false;
        OnAdChanged();
    }

    /// <summary>The ad changed: save it, update the totals, and stop posting if it isn't a complete ad anymore.</summary>
    private void OnAdChanged()
    {
        if (_adShownFor.Length == 0) return;
        var d = AdDraftFor(_adShownFor);
        d.Offer = AdOfferSlots.Where(s => s.Item != null).Select(s => s.Item!.AssetId).ToList();
        d.Request = AdWantSlots.Where(s => s.Item != null).Select(s => s.Item!.AssetId).ToList();
        d.Tags = AdWantSlots.Where(s => s.Tag != null).Select(s => s.Tag!.Tag).ToList();
        if (AdRunning && !(OfferCount > 0 && WantCount > 0)) Settings.TradeAdOn = false;
        _saveTimer.Stop();
        _saveTimer.Start();
        NotifyAd();
    }

    private void NotifyAd()
    {
        AdOfferValue = AdOfferSlots.Sum(s => s.Item?.Worth ?? 0).ToString("N0");
        AdWantValue = AdWantSlots.Sum(s => s.Item?.Worth ?? 0).ToString("N0");
        foreach (var n in new[] { nameof(HasRoliCookie), nameof(AdRunning), nameof(AdRunningAnywhere), nameof(CanStartAd) }) OnPropertyChanged(n);
        UpdateAdStatus();
    }

    // ================================================================== the verification cookie

    [RelayCommand]
    private void SaveRoliCookie()
    {
        var c = RolimonsAds.CleanVerification(RoliCookieInput ?? "");
        if (c.Length < 20) { StatusMessage = "That doesn't look like a Rolimons verification cookie."; return; }
        Settings.RoliVerification = RolimonsAds.Protect(c);
        RoliCookieInput = "";
        NotifyAd();
        StatusMessage = "Rolimons verification saved (encrypted on this PC).";
    }

    [RelayCommand]
    private void ForgetRoliCookie()
    {
        Settings.RoliVerification = "";
        Settings.TradeAdOn = false;
        NotifyAd();
    }

    // ================================================================== posting

    [RelayCommand]
    private void StartAd()
    {
        if (!CanStartAd || CurrentAccount is not { } a || a.Id != _adShownFor) return;
        if (Settings.TradeAdAccountId != a.Id)
        {
            // one ad at a time: it moves to this account (Rolimons' wait between ads is per account)
            Settings.TradeAdAccountId = a.Id;
            Settings.TradeAdLastPosted = DateTime.MinValue;
            _adRetryAt = DateTime.MinValue;
            AdLastResult = "";
        }
        Settings.TradeAdOn = true;
        NotifyAd();
        _ = AdTickAsync(force: true);
    }

    [RelayCommand]
    private void StopAd()
    {
        Settings.TradeAdOn = false;
        NotifyAd();
    }

    [RelayCommand]
    private Task PostAdNow() => AdTickAsync(force: true);

    private async Task AdTickAsync(bool force = false)
    {
        if (_adPosting) return;
        if (!force && !Settings.TradeAdOn) return;
        var minutes = Math.Max(RolimonsAds.MinMinutes, Settings.TradeAdMinutes);
        var due = Settings.TradeAdLastPosted.AddMinutes(minutes);
        if (DateTime.UtcNow < due || DateTime.UtcNow < _adRetryAt)
        {
            if (force) StatusMessage = $"Rolimons allows one ad every {RolimonsAds.MinMinutes} minutes. Next one {NextText()}.";
            UpdateAdStatus();
            return;
        }
        // the ad offers that account's items: never post it as another account
        var a = Settings.TradeAdAccountId.Length > 0 ? Accounts.FirstOrDefault(x => x.Id == Settings.TradeAdAccountId) : CurrentAccount;
        if (a == null && Settings.TradeAdOn)
        {
            Settings.TradeAdOn = false;
            AdLastResult = "The account this ad was for isn't in RoDeck anymore, so it stopped.";
            NotifyAd();
            return;
        }
        var cookie = RolimonsAds.Unprotect(Settings.RoliVerification);
        var ad = a == null ? null : AdDraftFor(a.Id);
        if (a == null || ad == null || cookie.Length == 0 || ad.Offer.Count == 0) { UpdateAdStatus(); return; }

        _adPosting = true;
        try
        {
            // Rolimons refuses the whole ad ("Invalid item ID") for an item it doesn't list, or one the account no longer owns:
            // leave those out instead of failing every time
            try { await LimitedValues.EnsureAsync(CancellationToken.None); } catch { }
            var offer = new List<long>();
            var skipped = new List<string>();
            List<LimitedTile>? mine = null;
            try { mine = await RobloxTrades.GetTradableAsync(a.Cookie, a.UserId, CancellationToken.None); } catch { }
            foreach (var id in ad.Offer)
            {
                var name = LimitedValues.Get(id)?.Name ?? mine?.FirstOrDefault(t => t.AssetId == id)?.Name ?? $"item {id}";
                if (LimitedValues.Count > 0 && LimitedValues.Get(id) == null) { skipped.Add($"{name} (not on Rolimons)"); continue; }
                if (mine is { Count: > 0 })
                {
                    var copies = mine.Where(t => t.AssetId == id).ToList();
                    if (copies.Count == 0) { skipped.Add($"{name} (not in this account's inventory)"); continue; }
                    if (copies.All(t => t.IsBundle)) { skipped.Add($"{name} (a bundle: Rolimons ads only take single items)"); continue; }
                    if (copies.All(t => t.IsOnHold)) { skipped.Add($"{name} (on trade hold)"); continue; }
                }
                offer.Add(id);
            }
            var want = ad.Request.Where(id => LimitedValues.Count == 0 || LimitedValues.Get(id) != null).ToList();
            string left = skipped.Count == 0 ? "" : " Left out: " + string.Join(", ", skipped) + ".";
            if (offer.Count == 0)
            {
                AdLastResult = "None of the items you offer can go in an ad right now." + left;
                Notify?.Invoke("Trade ad: " + AdLastResult);
                _adRetryAt = DateTime.UtcNow.AddMinutes(2);
                UpdateAdStatus();
                return;
            }
            var r = await RolimonsAds.PostAsync(cookie, a.UserId, offer, want, ad.Tags, CancellationToken.None);
            if (r.Ok)
            {
                Settings.TradeAdLastPosted = DateTime.UtcNow;
                Settings.TradeAdPosts++;
                _saveTimer.Stop();
                _saveTimer.Start();
                Log(ActivityLog.Trade, $"Trade ad posted on Rolimons for {a.Label}.");
            }
            else
            {
                // Rolimons' own timer between ads: look again every 30 seconds and post the moment it's allowed
                bool cooldown = r.WaitSeconds > 0 || r.Message.Contains("wait", StringComparison.OrdinalIgnoreCase)
                                || r.Message.Contains("cooldown", StringComparison.OrdinalIgnoreCase) || r.Message.Contains("too soon", StringComparison.OrdinalIgnoreCase)
                                || r.Message.Contains("minute", StringComparison.OrdinalIgnoreCase);
                _adRetryAt = DateTime.UtcNow.AddSeconds(cooldown ? 30 : 120);
                Log(ActivityLog.Error, "Trade ad: " + r.Message);
                if (r.Message.Contains("verification")) { Settings.TradeAdOn = false; Notify?.Invoke(r.Message); }
            }
            AdLastResult = r.Message + left;
        }
        finally
        {
            _adPosting = false;
            NotifyAd();
        }
    }

    private string NextText()
    {
        var minutes = Math.Max(RolimonsAds.MinMinutes, Settings.TradeAdMinutes);
        var next = Settings.TradeAdLastPosted.AddMinutes(minutes);
        if (_adRetryAt > next) next = _adRetryAt;
        var left = next - DateTime.UtcNow;
        return left <= TimeSpan.Zero ? "any moment" : left.TotalMinutes >= 1 ? $"in {(int)Math.Ceiling(left.TotalMinutes)} min" : $"in {(int)left.TotalSeconds} s";
    }

    private void UpdateAdStatus()
    {
        AdStatus = !HasRoliCookie ? "Add your Rolimons verification first."
                 : OfferCount == 0 ? "Click an Offer slot to pick one of your limiteds."
                 : WantCount == 0 ? "Click a Want slot to pick an item or a tag."
                 : Settings.TradeAdOn && !AdRunning ? $"Your ad for {Accounts.FirstOrDefault(x => x.Id == Settings.TradeAdAccountId)?.Label ?? "another account"} is running. Press Start to post this account's ad instead (one ad at a time)."
                 : Settings.TradeAdOn ? $"Posting every {Math.Max(RolimonsAds.MinMinutes, Settings.TradeAdMinutes)} min · next {NextText()} · {Settings.TradeAdPosts} posted so far"
                 : "Ready. Press Start to post it now and every few minutes after.";
    }
}

/// <summary>A request tag (Any, Upgrade, Demand…): RoDeck's own tile with a colour and an icon.</summary>
public partial class AdTag : ObservableObject
{
    public string Tag { get; init; } = "";
    public string Name { get; init; } = "";
    public string Upper => Name.ToUpperInvariant();
    public string Glyph => Tag switch
    {
        "any" => "", "demand" => "", "rares" => "", "robux" => "", "upgrade" => "",
        "downgrade" => "", "rap" => "", "wishlist" => "", "projecteds" => "", "adds" => "", _ => ""
    };
    /// <summary>The tile's colour (top, bottom) as hex.</summary>
    public string Top => Tag switch
    {
        "any" => "#8B5CF6", "demand" => "#F97316", "rares" => "#EAB308", "robux" => "#94A3B8", "upgrade" => "#22C55E",
        "downgrade" => "#3B82F6", "rap" => "#06B6D4", "wishlist" => "#EC4899", "projecteds" => "#FACC15", "adds" => "#EF4444", _ => "#64748B"
    };
}

/// <summary>One of the ad's 4 offer or 4 want slots: empty, an item, or (want slots) a tag.</summary>
public partial class AdSlot : ObservableObject
{
    public int Index { get; init; }
    public bool IsOffer { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasItem))] private LimitedTile? item;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasTag))] private AdTag? tag;
    public bool IsEmpty => Item == null && Tag == null;
    public bool HasItem => Item != null;
    public bool HasTag => Tag != null;
    /// <summary>The empty slot's word; "Offer" / "Want" unless set (the calculator says "Add").</summary>
    public string? Label { get; init; }
    public string EmptyText => Label ?? (IsOffer ? "Offer" : "Want");
    public void Clear() { Item = null; Tag = null; }
}
