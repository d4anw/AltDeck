using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AltDeck.Models;
using AltDeck.Services;

namespace AltDeck.ViewModels;

/// <summary>One account to add: its cookie, plus details an account file may carry.</summary>
public sealed record ImportItem(string Cookie, string? Alias = null, string? Notes = null, string? Group = null);

/// <summary>How a bulk add went.</summary>
public sealed record ImportResult(int Added, int Updated, int Failed, string? FirstError)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added > 0) parts.Add($"Added {Added} account{(Added == 1 ? "" : "s")}");
            if (Updated > 0) parts.Add($"{(parts.Count == 0 ? "Updated" : "updated")} {Updated} you already had");
            var text = parts.Count == 0 ? "No accounts were added." : string.Join(", ", parts) + ".";
            if (Failed > 0)
                text += $"\n\n{Failed} couldn't be added" + (FirstError != null ? $" ({FirstError.TrimEnd('.')})." : ".");
            return text;
        }
    }
}

/// <summary>Adding many accounts at once: pasted cookies, or an account file (JSON list or plain text).</summary>
public partial class MainViewModel
{
    static readonly Regex CookieRx = new(@"_\|WARNING:[^\s""';,]+", RegexOptions.Compiled);

    /// <summary>Every cookie in some pasted text: anything with Roblox's warning prefix, or else one per line.</summary>
    public static List<string> FindCookies(string text)
    {
        var found = CookieRx.Matches(text).Select(m => m.Value).ToList();
        if (found.Count == 0)
            found = text.Split('\n').Select(RobloxApi.CleanCookie).Where(c => c.Length >= 100).ToList();
        return found.Distinct().ToList();
    }

    /// <summary>Reads an account file. A JSON list of accounts (cookie under SecurityToken or Cookie, with optional
    /// Alias, Description / Notes and Group), or any text with cookies in it. Passwords in the file are ignored.</summary>
    public static (List<ImportItem> Items, string? Error) ReadAccountFile(string path)
    {
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path)).Trim().TrimStart('﻿'); }
        catch (DecoderFallbackException) { return (new(), Encrypted); }
        catch (Exception ex) { return (new(), "Couldn't read the file: " + ex.Message); }

        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            try
            {
                var root = JsonNode.Parse(text);
                var list = root as JsonArray ?? Prop(root as JsonObject, "Accounts") as JsonArray;
                var items = new List<ImportItem>();
                foreach (var node in list ?? new JsonArray())
                {
                    if (node is not JsonObject o) continue;
                    var cookie = Str(o, "SecurityToken") ?? Str(o, "Cookie") ?? Str(o, "ROBLOSECURITY");
                    if (string.IsNullOrWhiteSpace(cookie)) continue;
                    items.Add(new ImportItem(cookie, Str(o, "Alias"), Str(o, "Description") ?? Str(o, "Notes"), Str(o, "Group")));
                }
                if (items.Count > 0) return (items, null);
            }
            catch (JsonException) { }   // not valid JSON: look for cookies in the text instead
        }

        var cookies = FindCookies(text);
        if (cookies.Count > 0) return (cookies.Select(c => new ImportItem(c)).ToList(), null);
        return (new(), LooksBinary(text) ? Encrypted : "No accounts found in that file.");
    }

    const string Encrypted =
        "This file is encrypted, so AltDeck can't read it. Turn off encryption in the program that saved it, let it save the file again, then import that.";

    static bool LooksBinary(string s) => s.Take(200).Count(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t')) > 5;

    static JsonNode? Prop(JsonObject? o, string name) =>
        o?.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    static string? Str(JsonObject o, string name)
    {
        var v = Prop(o, name);
        if (v is not JsonValue jv || !jv.TryGetValue<string>(out var s)) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Signs in with one cookie and adds the account, or refreshes the one already there. No saving, no toasts.</summary>
    private async Task<(Account? Account, bool IsNew, string? Error)> AddOrUpdateCoreAsync(string rawCookie)
    {
        var cookie = RobloxApi.CleanCookie(rawCookie);
        if (cookie.Length == 0) return (null, false, "The cookie is empty.");
        var user = await RobloxApi.GetAuthenticatedUserAsync(cookie);
        if (user == null) return (null, false, "That cookie isn't valid, or it has expired.");

        var acc = Accounts.FirstOrDefault(a => a.UserId == user.Id);
        bool isNew = acc == null;
        if (acc == null)
        {
            acc = new Account { UserId = user.Id };
            Accounts.Add(acc);
            HookAccount(acc);
        }
        acc.Cookie = cookie;
        acc.Username = user.Name;
        acc.DisplayName = user.DisplayName;
        acc.AvatarUrl = await RobloxApi.GetAvatarUrlAsync(user.Id);
        await CheckAccountAsync(acc);
        return (acc, isNew, null);
    }

    /// <summary>Adds many accounts one after another (spaced out so Roblox doesn't rate-limit), then saves once.</summary>
    public async Task<ImportResult> ImportAccountsAsync(IReadOnlyList<ImportItem> items)
    {
        int added = 0, updated = 0, failed = 0;
        string? firstError = null;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            StatusMessage = $"Adding account {i + 1} of {items.Count}…";
            try
            {
                var (acc, isNew, error) = await AddOrUpdateCoreAsync(item.Cookie);
                if (acc == null) { failed++; firstError ??= error; }
                else
                {
                    if (isNew) added++; else updated++;
                    // details from a file only fill what's empty, so nothing you set in AltDeck is overwritten
                    if (string.IsNullOrWhiteSpace(acc.Alias) && !string.IsNullOrWhiteSpace(item.Alias)) acc.Alias = item.Alias;
                    if (string.IsNullOrWhiteSpace(acc.Notes) && !string.IsNullOrWhiteSpace(item.Notes)) acc.Notes = item.Notes;
                    if (string.IsNullOrWhiteSpace(acc.Groups) && !string.IsNullOrWhiteSpace(item.Group)
                        && !item.Group.Equals("Default", StringComparison.OrdinalIgnoreCase))
                        acc.Groups = item.Group.Replace(",", " ");
                }
            }
            catch (Exception ex) { failed++; firstError ??= ex.Message; }

            if (i < items.Count - 1) await Task.Delay(400);
        }

        try { _vault.Save(Accounts); } catch { }
        RebuildGroups();
        UpdatePicked();
        AccountsView.Refresh();
        UpdateHomeTexts();

        var result = new ImportResult(added, updated, failed, firstError);
        StatusMessage = result.Summary.Split('\n')[0];
        Log(failed > 0 && added + updated == 0 ? ActivityLog.Error : ActivityLog.Info,
            $"Bulk add: {added} added, {updated} updated, {failed} failed.");
        return result;
    }
}
