using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using RoDeck.Models;
using RoDeck.Services;

namespace RoDeck.ViewModels;

// Copy link / Open on roblox.com for anything with a page on roblox.com: games, players, communities and items.
public partial class MainViewModel
{
    /// <summary>The roblox.com page of a game, player, community or item, or "" when it has none.</summary>
    public static string RobloxLink(object? x) => x switch
    {
        GameTile { PlaceId: > 0 } g => $"https://www.roblox.com/games/{g.PlaceId}",
        GameDetails { PlaceId: > 0 } g => $"https://www.roblox.com/games/{g.PlaceId}",
        PlayerProfile { UserId: > 0 } p => $"https://www.roblox.com/users/{p.UserId}/profile",
        FriendTile { UserId: > 0 } f => $"https://www.roblox.com/users/{f.UserId}/profile",
        CommunityMember { UserId: > 0 } m => $"https://www.roblox.com/users/{m.UserId}/profile",
        CommunityTile { Id: > 0 } c => $"https://www.roblox.com/communities/{c.Id}",
        CommunityDetails { Id: > 0 } c => $"https://www.roblox.com/communities/{c.Id}",
        MarketItem { Id: > 0 } i => i.IsBundle ? $"https://www.roblox.com/bundles/{i.Id}" : $"https://www.roblox.com/catalog/{i.Id}",
        WardrobeItem { AssetId: > 0 } i => $"https://www.roblox.com/catalog/{i.AssetId}",
        GamePassInfo { Id: > 0 } p => $"https://www.roblox.com/game-pass/{p.Id}",
        _ => ""
    };

    /// <summary>Copies the roblox.com link, to paste to a friend.</summary>
    [RelayCommand]
    private void CopyLink(object? x)
    {
        var url = RobloxLink(x ?? CurrentLinkTarget());
        if (url.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(url);
            StatusMessage = "Link copied: " + url;
        }
        catch { StatusMessage = "Couldn't copy the link (another app is using the clipboard). Try again."; }
    }

    [RelayCommand]
    private void OpenLinkOnWeb(object? x)
    {
        var url = RobloxLink(x ?? CurrentLinkTarget());
        if (url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Without a parameter: what's open on the page you're on (a game, a profile, a community, an item).</summary>
    private object? CurrentLinkTarget() => Page switch
    {
        "Games" when IsGameOpen => OpenGame,
        "Friends" when IsProfileOpen => OpenProfile,
        "Communities" when IsCommunityOpen => OpenCommunity,
        "Marketplace" => OpenMarketItem,
        _ => null
    };
}
