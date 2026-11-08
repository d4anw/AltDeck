using System.Windows.Threading;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

/// <summary>
/// Windows notifications for the account top right: a friend comes online, a trade comes in, a friend request comes in,
/// and big deals on limiteds. Every one is OFF until turned on in Settings → Notifications.
/// The first check after starting (or switching accounts) only notes what's already there, so you're never flooded
/// with old trades or requests.
/// </summary>
public partial class MainViewModel
{
    /// <summary>(title, text, page to open when the notification is clicked).</summary>
    public event Action<string, string, string>? Notification;

    private readonly DispatcherTimer _notifyTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private int _notifyTick;
    private bool _notifyBusy;
    private Account? _notifyFor;
    private Dictionary<long, int>? _friendPresence;      // user ID → presence type at the last check
    private HashSet<long>? _seenTrades, _seenRequests;
    private readonly HashSet<string> _seenDealNotes = new();

    public bool AnyNotifications => Settings.NotifyFriendOnline || Settings.NotifyTrades || Settings.NotifyFriendRequests || Settings.NotifyDeals;

    /// <summary>For the tray panel: which notifications are on.</summary>
    public string TrayNotifyText
    {
        get
        {
            var on = new List<string>();
            if (Settings.NotifyFriendOnline) on.Add("friends online");
            if (Settings.NotifyTrades) on.Add("trades");
            if (Settings.NotifyFriendRequests) on.Add("requests");
            if (Settings.NotifyDeals) on.Add("deals");
            return on.Count == 0 ? "Notifications are off" : "Notifying about " + string.Join(", ", on);
        }
    }

    /// <summary>Started once; does nothing while every notification is off.</summary>
    private void SetupNotifications()
    {
        _notifyTimer.Tick += async (_, _) => await NotifyTickAsync();
        _notifyTimer.Start();
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName?.StartsWith("Notify") != true) return;
            OnPropertyChanged(nameof(AnyNotifications));
            OnPropertyChanged(nameof(TrayNotifyText));
            // a switch turned on: start fresh, so what's already there isn't announced
            if (e.PropertyName == nameof(AppSettings.NotifyFriendOnline)) _friendPresence = null;
            if (e.PropertyName == nameof(AppSettings.NotifyTrades)) _seenTrades = null;
            if (e.PropertyName == nameof(AppSettings.NotifyFriendRequests)) _seenRequests = null;
        };
    }

    private async Task NotifyTickAsync()
    {
        if (_notifyBusy || !AnyNotifications || CurrentAccount is not { } a || string.IsNullOrEmpty(a.Cookie)) return;
        _notifyBusy = true;
        try
        {
            if (_notifyFor != a) { _notifyFor = a; _friendPresence = null; _seenTrades = null; _seenRequests = null; }
            _notifyTick++;
            if (Settings.NotifyDeals) await CheckDealsAsync();                                 // every 5 s
            if (_notifyTick % 12 != 1) return;                                                  // the rest once a minute
            if (Settings.NotifyFriendOnline) await CheckFriendsOnlineAsync(a);
            if (Settings.NotifyTrades) await CheckTradesAsync(a);
            if (Settings.NotifyFriendRequests) await CheckRequestsAsync(a);
        }
        catch (RobloxException) { }   // Roblox busy or signed out: try again next time
        catch (System.Text.Json.JsonException) { }
        finally { _notifyBusy = false; }
    }

    private async Task CheckFriendsOnlineAsync(Account a)
    {
        var friends = await GetFriendsAsync(a);
        if (a != CurrentAccount) return;
        var now = friends.ToDictionary(f => f.UserId, f => f.PresenceType);
        if (_friendPresence != null)
        {
            var cameOnline = friends.Where(f => f.PresenceType >= 1 && _friendPresence.TryGetValue(f.UserId, out var was) && was == 0)
                                    .Where(f => !Settings.NotifyFavouritesOnly || f.IsFavourite)
                                    .ToList();
            if (cameOnline.Count == 1)
            {
                var f = cameOnline[0];
                Notification?.Invoke($"{f.Label} is online", f.PresenceType == 2 && f.Location.Length > 0 ? $"Playing {f.Location}" : "Just came online", "Friends");
            }
            else if (cameOnline.Count > 1)
                Notification?.Invoke($"{cameOnline.Count} friends came online",
                    string.Join(", ", cameOnline.Take(4).Select(f => f.Label)) + (cameOnline.Count > 4 ? "…" : ""), "Friends");
        }
        _friendPresence = now;
    }

    private async Task CheckTradesAsync(Account a)
    {
        var (rows, _) = await RobloxTrades.GetTradesAsync(a.Cookie, "Inbound", null, CancellationToken.None);
        if (a != CurrentAccount) return;
        if (_seenTrades != null)
        {
            var fresh = rows.Where(r => !_seenTrades.Contains(r.Id)).ToList();
            foreach (var r in fresh.Take(3))
            {
                string text = "Open it in RoDeck to see both sides.";
                try
                {
                    await LimitedValues.EnsureAsync(CancellationToken.None);
                    var d = await RobloxTrades.GetTradeAsync(a.Cookie, r.Id, a.UserId, CancellationToken.None);
                    long diff = d.Difference;
                    text = $"You give {d.Give:N0} · you get {d.Get:N0} ({(diff >= 0 ? "+" : "−")}{Math.Abs(diff):N0} value)";
                }
                catch (RobloxException) { }
                Notification?.Invoke($"New trade from {r.PartnerLabel}", text, "Trades");
            }
            if (fresh.Count > 3) Notification?.Invoke($"{fresh.Count - 3} more new trades", "Open RoDeck to see them.", "Trades");
            if (fresh.Count > 0) _ = RefreshInboundCountAsync();
        }
        _seenTrades = rows.Select(r => r.Id).ToHashSet();
    }

    private async Task CheckRequestsAsync(Account a)
    {
        var list = await RobloxFriends.GetRequestsAsync(a.Cookie, CancellationToken.None);
        if (a != CurrentAccount) return;
        if (_seenRequests != null)
        {
            var fresh = list.Where(r => !_seenRequests.Contains(r.UserId)).ToList();
            if (fresh.Count == 1)
                Notification?.Invoke("New friend request", $"{(string.IsNullOrWhiteSpace(fresh[0].DisplayName) ? fresh[0].Name : fresh[0].DisplayName)} wants to be friends", "Requests");
            else if (fresh.Count > 1)
                Notification?.Invoke($"{fresh.Count} new friend requests", "Open RoDeck to accept or decline them.", "Requests");
            if (fresh.Count > 0) _ = RefreshRequestCountAsync();
        }
        _seenRequests = list.Select(r => r.UserId).ToHashSet();
    }

    /// <summary>New listings at least the chosen % below RAP (or value, as picked on Deals).</summary>
    private async Task CheckDealsAsync()
    {
        await LimitedValues.EnsureAsync(CancellationToken.None, maxAge: TimeSpan.FromMinutes(2));
        var fresh = await LimitedValues.GetNewPricesAsync(CancellationToken.None);
        int min = Math.Clamp(Settings.NotifyDealPercent, 5, 90);
        foreach (var (id, price, at) in fresh)
        {
            if (DateTime.Now - at > TimeSpan.FromMinutes(2)) continue;   // only what's new right now
            if (!_seenDealNotes.Add($"{id}:{price}")) continue;
            var v = LimitedValues.Get(id);
            if (v == null) continue;
            bool byRap = DealBasis == "RAP" || !v.HasValue;
            if (byRap && v.Projected) continue;                         // a pushed-up RAP isn't a real deal
            long worth = byRap ? v.Rap : v.Value;
            if (worth <= 0) continue;
            double off = 1 - (double)price / worth;
            if (off * 100 < min) continue;
            if (Settings.NotifyDealsAffordable && CurrentAccount?.Robux is long r && price > r) continue;
            Notification?.Invoke($"Deal: {v.Name} {off:P0} below {(byRap ? "RAP" : "value")}",
                                 $"R$ {price:N0} (RAP {v.Rap:N0}{(v.HasValue ? $", value {v.Value:N0}" : "")})", "Deals");
        }
        if (_seenDealNotes.Count > 2000) _seenDealNotes.Clear();
    }

    /// <summary>A notification was clicked: show what it was about.</summary>
    public void OpenNotification(string where)
    {
        switch (where)
        {
            case "Friends": Page = "Friends"; FriendsTab = "Friends"; break;
            case "Requests": Page = "Friends"; FriendsTab = "Requests"; break;
            case "Trades": Page = "Trades"; TradesTab = "Trades"; TradeType = "Inbound"; break;
            case "Deals": Page = "Trades"; TradesTab = "Deals"; break;
        }
    }
}
