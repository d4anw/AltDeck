using System.Collections.ObjectModel;
using System.Windows.Threading;
using RoDeck.Models;
using RoDeck.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RoDeck.ViewModels;

/// <summary>
/// Trades → Trade ads: one Rolimons trade ad (up to 4 items offered; up to 4 items or tags asked for) that RoDeck
/// posts again every 15+ minutes while it runs, also from the tray. Off until you start it.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<LimitedTile> AdOffer { get; } = new();
    public ObservableCollection<LimitedTile> AdRequest { get; } = new();
    public ObservableCollection<AdTag> AdTags { get; } = new();
    public ObservableCollection<LimitedTile> AdRequestHits { get; } = new();
    [ObservableProperty] private string adRequestSearch = "";
    [ObservableProperty] private string adStatus = "";
    [ObservableProperty] private string roliCookieInput = "";
    public bool HasRoliCookie => Settings.RoliVerification.Length > 0;
    public bool AdRunning => Settings.TradeAdOn;
    public int AdRequestCount => AdRequest.Count + AdTags.Count(t => t.IsSelected);
    public bool CanStartAd => HasRoliCookie && AdOffer.Count > 0 && AdRequestCount > 0;

    private readonly DispatcherTimer _adTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _adPosting, _adSetUp;
    private DateTime _adRetryAt = DateTime.MinValue;

    /// <summary>Called once at start: the tags, the saved ad, and the timer (which does nothing while the ad is off).</summary>
    private void SetupTradeAds()
    {
        foreach (var (tag, name) in RolimonsAds.Tags)
        {
            var t = new AdTag { Tag = tag, Name = name, IsSelected = Settings.TradeAdTags.Contains(tag) };
            t.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AdTag.IsSelected)) OnAdChanged(); };
            AdTags.Add(t);
        }
        _adTimer.Tick += async (_, _) => await AdTickAsync();
        _adTimer.Start();
    }

    /// <summary>When the Trade ads tab opens: show the saved ad's items with their values and pictures.</summary>
    private void LoadTradeAdTab()
    {
        if (!_adSetUp)
        {
            _adSetUp = true;
            foreach (var id in Settings.TradeAdOffer) AdOffer.Add(LimitedTile.From(id, $"Item {id}", 0));
            foreach (var id in Settings.TradeAdRequest) AdRequest.Add(LimitedTile.From(id, $"Item {id}", 0));
            _ = FillLimitedThumbsAsync(AdOffer.Concat(AdRequest).ToList());
            if (CurrentAccount is { } a && _myTradableAll.Count == 0) _ = LoadMyTradableAsync(a, null);
        }
        UpdateAdStatus();
    }

    // ---- building the ad

    /// <summary>Click one of your limiteds: in or out of the offer (up to 4, no duplicates).</summary>
    [RelayCommand]
    private void ToggleAdOffer(LimitedTile? t)
    {
        if (t == null) return;
        var have = AdOffer.FirstOrDefault(x => x.AssetId == t.AssetId);
        if (have != null) AdOffer.Remove(have);
        else if (AdOffer.Count >= 4) { StatusMessage = "Rolimons allows up to 4 items in the offer."; return; }
        else AdOffer.Add(t);
        OnAdChanged();
    }

    partial void OnAdRequestSearchChanged(string value)
    {
        AdRequestHits.Clear();
        var q = (value ?? "").Trim();
        if (q.Length < 2) return;
        var hits = LimitedValues.All
            .Where(v => v.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Acronym, q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Worth).Take(12)
            .Select(v => LimitedTile.From(v.AssetId, v.Name, v.Rap)).ToList();
        foreach (var h in hits) AdRequestHits.Add(h);
        _ = FillLimitedThumbsAsync(hits);
    }

    [RelayCommand]
    private void ToggleAdRequest(LimitedTile? t)
    {
        if (t == null) return;
        var have = AdRequest.FirstOrDefault(x => x.AssetId == t.AssetId);
        if (have != null) AdRequest.Remove(have);
        else if (AdRequestCount >= 4) { StatusMessage = "Rolimons allows up to 4 things asked for (items and tags together)."; return; }
        else AdRequest.Add(t);
        OnAdChanged();
    }

    [RelayCommand]
    private void ToggleAdTag(AdTag? t)
    {
        if (t == null) return;
        if (!t.IsSelected && AdRequestCount >= 4) { StatusMessage = "Rolimons allows up to 4 things asked for (items and tags together)."; return; }
        t.IsSelected = !t.IsSelected;
    }

    /// <summary>The ad changed: save it, and stop posting if it isn't a complete ad anymore.</summary>
    private void OnAdChanged()
    {
        Settings.TradeAdOffer = AdOffer.Select(x => x.AssetId).ToList();
        Settings.TradeAdRequest = AdRequest.Select(x => x.AssetId).ToList();
        Settings.TradeAdTags = AdTags.Where(t => t.IsSelected).Select(t => t.Tag).ToList();
        if (Settings.TradeAdOn && !(AdOffer.Count > 0 && AdRequestCount > 0)) Settings.TradeAdOn = false;
        _saveTimer.Stop();
        _saveTimer.Start();
        NotifyAd();
    }

    private void NotifyAd()
    {
        foreach (var n in new[] { nameof(HasRoliCookie), nameof(AdRunning), nameof(AdRequestCount), nameof(CanStartAd) }) OnPropertyChanged(n);
        UpdateAdStatus();
    }

    // ---- the verification cookie

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

    // ---- posting

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

    [ObservableProperty] private string adLastResult = "";

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
                 : AdOffer.Count == 0 ? "Pick up to 4 of your limiteds to offer."
                 : AdRequestCount == 0 ? "Pick what you want: items or tags (up to 4)."
                 : Settings.TradeAdOn ? $"Posting every {Math.Max(RolimonsAds.MinMinutes, Settings.TradeAdMinutes)} min · next {NextText()} · {Settings.TradeAdPosts} posted so far"
                 : "Ready. Press Start to post it now and every few minutes after.";
    }
}

/// <summary>A request tag chip on the Trade ads tab.</summary>
public partial class AdTag : ObservableObject
{
    public string Tag { get; init; } = "";
    public string Name { get; init; } = "";
    [ObservableProperty] private bool isSelected;
}
