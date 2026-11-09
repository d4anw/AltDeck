using System.Collections.ObjectModel;
using System.Windows.Threading;
using RoDeck.Models;
using RoDeck.Services;
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
    public bool AdRunning => Settings.TradeAdOn;
    private int OfferCount => AdOfferSlots.Count(s => s.Item != null);
    private int WantCount => AdWantSlots.Count(s => !s.IsEmpty);
    public bool CanStartAd => HasRoliCookie && OfferCount > 0 && WantCount > 0;

    private readonly DispatcherTimer _adTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _adPosting, _adSetUp;
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
        _adTimer.Tick += async (_, _) => await AdTickAsync();
        _adTimer.Start();
    }

    /// <summary>When the Trade ads tab opens: fill the slots from the saved ad (with values and pictures).</summary>
    private void LoadTradeAdTab()
    {
        if (!_adSetUp)
        {
            _adSetUp = true;
            var tiles = new List<LimitedTile>();
            for (int i = 0; i < 4 && i < Settings.TradeAdOffer.Count; i++)
                tiles.Add(AdOfferSlots[i].Item = LimitedTile.From(Settings.TradeAdOffer[i], $"Item {Settings.TradeAdOffer[i]}", 0));
            int w = 0;
            foreach (var id in Settings.TradeAdRequest.Take(4)) tiles.Add(AdWantSlots[w++].Item = LimitedTile.From(id, $"Item {id}", 0));
            foreach (var tag in Settings.TradeAdTags)
                if (w < 4 && AdTags.FirstOrDefault(t => t.Tag == tag) is { } t) AdWantSlots[w++].Tag = t;
            _ = FillLimitedThumbsAsync(tiles);
            if (CurrentAccount is { } a && _myTradableAll.Count == 0) _ = LoadMyTradableAsync(a, null);
        }
        NotifyAd();
    }

    // ================================================================== the picker

    [ObservableProperty] private bool isAdPickerOpen;
    [ObservableProperty] private AdSlot? adPickerSlot;
    [ObservableProperty] private string adPickerSearch = "";
    [ObservableProperty] private bool adPickerAscending;
    [ObservableProperty] private string adPickerMin = "", adPickerMax = "";
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
        FillAdPicker();
    }

    [RelayCommand] private void CloseAdPicker() => IsAdPickerOpen = false;

    [RelayCommand] private void SortAdPicker(string? dir) => AdPickerAscending = dir == "asc";

    private void FillAdPicker()
    {
        if (AdPickerSlot is not { } slot) return;
        var q = (AdPickerSearch ?? "").Trim();
        long? min = long.TryParse(AdPickerMin, out var mn) ? mn : null, max = long.TryParse(AdPickerMax, out var mx) ? mx : null;
        IEnumerable<LimitedTile> source = slot.IsOffer
            ? _myTradableAll.GroupBy(t => t.AssetId).Select(g => g.First())               // one tile per item you own
            : LimitedValues.All.Select(v => LimitedTile.From(v.AssetId, v.Name, v.Rap));
        var list = source.Where(t =>
                (q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(LimitedValues.Get(t.AssetId)?.Acronym, q, StringComparison.OrdinalIgnoreCase))
                && (min == null || t.Worth >= min) && (max == null || t.Worth <= max));
        list = AdPickerAscending ? list.OrderBy(t => t.Worth) : list.OrderByDescending(t => t.Worth);
        var shown = list.Take(120).ToList();
        AdPickerItems.Clear();
        foreach (var t in shown) AdPickerItems.Add(t);
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
        Settings.TradeAdOffer = AdOfferSlots.Where(s => s.Item != null).Select(s => s.Item!.AssetId).ToList();
        Settings.TradeAdRequest = AdWantSlots.Where(s => s.Item != null).Select(s => s.Item!.AssetId).ToList();
        Settings.TradeAdTags = AdWantSlots.Where(s => s.Tag != null).Select(s => s.Tag!.Tag).ToList();
        if (Settings.TradeAdOn && !(OfferCount > 0 && WantCount > 0)) Settings.TradeAdOn = false;
        _saveTimer.Stop();
        _saveTimer.Start();
        NotifyAd();
    }

    private void NotifyAd()
    {
        AdOfferValue = AdOfferSlots.Sum(s => s.Item?.Worth ?? 0).ToString("N0");
        AdWantValue = AdWantSlots.Sum(s => s.Item?.Worth ?? 0).ToString("N0");
        foreach (var n in new[] { nameof(HasRoliCookie), nameof(AdRunning), nameof(CanStartAd) }) OnPropertyChanged(n);
        UpdateAdStatus();
    }

    // ================================================================== the verification cookie

    [RelayCommand]
    private void SaveRoliCookie()
    {
        var c = (RoliCookieInput ?? "").Trim();
        if (c.StartsWith("_RoliVerification=", StringComparison.OrdinalIgnoreCase)) c = c["_RoliVerification=".Length..];
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
        if (!CanStartAd || CurrentAccount is not { } a) return;
        Settings.TradeAdAccountId = a.Id;
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
        var a = Accounts.FirstOrDefault(x => x.Id == Settings.TradeAdAccountId) ?? CurrentAccount;
        var cookie = RolimonsAds.Unprotect(Settings.RoliVerification);
        if (a == null || cookie.Length == 0 || Settings.TradeAdOffer.Count == 0) { UpdateAdStatus(); return; }

        _adPosting = true;
        try
        {
            var r = await RolimonsAds.PostAsync(cookie, a.UserId, Settings.TradeAdOffer, Settings.TradeAdRequest, Settings.TradeAdTags, CancellationToken.None);
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
                _adRetryAt = DateTime.UtcNow.AddSeconds(Math.Max(r.WaitSeconds, 120));
                Log(ActivityLog.Error, "Trade ad: " + r.Message);
                if (r.Message.Contains("verification")) { Settings.TradeAdOn = false; Notify?.Invoke(r.Message); }
            }
            AdLastResult = r.Message;
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
