using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

/// <summary>
/// One friends list per account, shared by Home, the Friends page and the server finder, so Roblox is asked once
/// instead of three times. Two pages asking at the same moment share one request. The list (names) is kept 5 minutes,
/// who's online 45 seconds; the same tile objects are reused, so a status update shows everywhere at once.
/// </summary>
public partial class MainViewModel
{
    private sealed class FriendsEntry
    {
        public List<FriendTile> List = new();
        public DateTime ListAt = DateTime.MinValue;
        public DateTime PresenceAt = DateTime.MinValue;
        public Task<List<FriendTile>>? Running;
    }

    private readonly Dictionary<string, FriendsEntry> _friendsStore = new();
    private static readonly TimeSpan FriendListFresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PresenceFresh = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The account's friends with status. <paramref name="namesReady"/> is called as soon as the names are known
    /// (before status), so a page can show the list right away. <paramref name="force"/>: ask Roblox again (Refresh).
    /// </summary>
    private Task<List<FriendTile>> GetFriendsAsync(Account a, Action<List<FriendTile>>? namesReady = null, bool force = false)
    {
        if (!_friendsStore.TryGetValue(a.Id, out var e)) _friendsStore[a.Id] = e = new FriendsEntry();
        if (force) { e.ListAt = DateTime.MinValue; e.PresenceAt = DateTime.MinValue; }

        // someone is already loading this account's friends: wait for the same result
        if (e.Running is { IsCompleted: false } running)
        {
            if (e.List.Count > 0) namesReady?.Invoke(e.List);
            return running;
        }

        bool listFresh = e.List.Count > 0 && DateTime.Now - e.ListAt < FriendListFresh;
        bool presenceFresh = listFresh && DateTime.Now - e.PresenceAt < PresenceFresh;
        if (presenceFresh)
        {
            namesReady?.Invoke(e.List);
            return Task.FromResult(e.List);
        }

        e.Running = LoadAsync();
        return e.Running;

        async Task<List<FriendTile>> LoadAsync()
        {
            if (!listFresh)
            {
                var fresh = await RobloxFriends.GetFriendListAsync(a.Cookie, a.UserId, CancellationToken.None);
                // keep what we already know about people (picture, favourite, last status) so nothing flickers
                var old = e.List.ToDictionary(t => t.UserId);
                foreach (var t in fresh)
                {
                    t.IsFavourite = Settings.FavouriteFriends.Contains(t.UserId);
                    if (old.TryGetValue(t.UserId, out var o))
                    {
                        t.HeadshotUrl = o.HeadshotUrl;
                        t.Location = o.Location; t.PlaceId = o.PlaceId; t.JobId = o.JobId; t.PresenceType = o.PresenceType;
                    }
                }
                e.List = fresh;
                e.ListAt = DateTime.Now;
            }
            namesReady?.Invoke(e.List);

            await RobloxFriends.FillPresenceAsync(a.Cookie, e.List, CancellationToken.None);
            e.PresenceAt = DateTime.Now;
            return e.List;
        }
    }

    /// <summary>After unfriending / accepting: the next look asks Roblox again.</summary>
    private void ForgetFriends(Account a)
    {
        if (_friendsStore.TryGetValue(a.Id, out var e)) { e.ListAt = DateTime.MinValue; e.PresenceAt = DateTime.MinValue; }
    }
}
