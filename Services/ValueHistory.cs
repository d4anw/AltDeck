using System.IO;
using System.Text.Json;
using RoDeck.Models;

namespace RoDeck.Services;

/// <summary>One day of someone's limiteds: what they were worth (value) and their RAP.</summary>
public readonly record struct ValuePoint(DateTime Day, long Value, long Rap);

/// <summary>
/// Someone's inventory value over time, two ways:
///  - rebuilt: for every limited they own now, from the day they got it (Roblox's inventory says when), its RAP worked
///    out from Roblox's daily sales the way Roblox moves RAP (10 % towards each sale). Items they traded away aren't
///    known, so the far past can come out lower than it really was.
///  - recorded: a real snapshot saved on this PC each day a profile (or one of your accounts) is looked at. Recorded
///    days replace rebuilt ones, so the graph gets exact from the first snapshot on.
/// </summary>
public static class ValueHistory
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoDeck", "history");

    private sealed record Snap(DateTime Day, long Value, long Rap);

    public static List<ValuePoint> LoadSnapshots(long userId)
    {
        try
        {
            var file = Path.Combine(Dir, $"{userId}.json");
            if (!File.Exists(file)) return new();
            var list = JsonSerializer.Deserialize<List<Snap>>(File.ReadAllText(file)) ?? new();
            return list.Select(s => new ValuePoint(s.Day.Date, s.Value, s.Rap)).OrderBy(p => p.Day).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Saves today's numbers for this user (replacing an earlier one from today).</summary>
    public static void Record(long userId, long value, long rap)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var list = LoadSnapshots(userId).Where(p => p.Day != DateTime.UtcNow.Date)
                                            .Select(p => new Snap(p.Day, p.Value, p.Rap)).ToList();
            list.Add(new Snap(DateTime.UtcNow.Date, value, rap));
            File.WriteAllText(Path.Combine(Dir, $"{userId}.json"), JsonSerializer.Serialize(list.OrderBy(s => s.Day)));
        }
        catch { }   // the graph still works without it
    }

    /// <summary>
    /// The rebuilt history (daily, from the first day they got one of their current limiteds until today), with the
    /// recorded snapshots laid over it. <paramref name="note"/> says how it was made.
    /// </summary>
    public static async Task<(List<ValuePoint> Points, string Note)> BuildAsync(string cookie, long userId, IReadOnlyList<LimitedTile> items,
                                                                             CancellationToken ct, Action<string>? progress = null)
    {
        var today = DateTime.UtcNow.Date;
        Dictionary<long, List<DateTime>> acquired;
        try { acquired = await RobloxTrades.GetAcquiredAsync(cookie, userId, ct); }
        catch (RobloxException ex) when (!ex.TooMany) { acquired = new(); }

        // per item: every copy and when it was got (oldest dates go to the first copies; unknown = had it all along)
        var groups = items.GroupBy(i => (i.AssetId, i.IsBundle)).Select(g =>
        {
            long lookup = g.Key.IsBundle && RobloxTrades.BundleParts.TryGetValue(g.Key.AssetId, out var part) ? part : g.Key.AssetId;
            var dates = acquired.TryGetValue(lookup, out var d) ? d.OrderBy(x => x).ToList() : new List<DateTime>();
            var copies = g.Select((t, i) => i < dates.Count ? dates[i].Date : DateTime.MinValue).ToList();
            var first = g.First();
            return new Group(g.Key.AssetId, g.Key.IsBundle, first.Rap, first.HasValue ? first.Value : -1, copies);
        }).OrderByDescending(g => g.Rap * g.Copies.Count).ToList();

        var known = groups.SelectMany(g => g.Copies).Where(d => d != DateTime.MinValue).ToList();
        var start = known.Count > 0 ? known.Min() : today.AddDays(-365);
        if (start > today.AddDays(-7)) start = today.AddDays(-7);
        int days = (int)(today - start).TotalDays;
        var value = new double[days + 1];
        var rap = new double[days + 1];

        // the most valuable items get their real RAP over time (one request each); the rest keep today's RAP
        const int withHistory = 40;
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            double[] series;
            if (gi < withHistory)
            {
                progress?.Invoke($"Working out their value over time… {gi + 1} of {Math.Min(withHistory, groups.Count)}");
                List<RobloxTrades.DaySales> sales;
                try { sales = await RobloxTrades.GetDailySalesAsync(g.AssetId, g.IsBundle, ct); }
                catch (RobloxException ex) when (!ex.TooMany) { sales = new(); }
                series = RapSeries(sales, start, days, g.Rap);
            }
            else series = Enumerable.Repeat((double)g.Rap, days + 1).ToArray();

            foreach (var got in g.Copies)
            {
                int from = got == DateTime.MinValue ? 0 : Math.Clamp((int)(got - start).TotalDays, 0, days);
                for (int d = from; d <= days; d++)
                {
                    rap[d] += series[d];
                    value[d] += g.Value > 0 ? g.Value : series[d];   // valued items keep their value (Rolimons' history isn't public)
                }
            }
        }

        var points = new SortedDictionary<DateTime, ValuePoint>();
        for (int d = 0; d <= days; d++)
            points[start.AddDays(d)] = new ValuePoint(start.AddDays(d), (long)Math.Round(value[d]), (long)Math.Round(rap[d]));
        // today: exactly what they have now
        points[today] = new ValuePoint(today, items.Sum(i => i.Worth), items.Sum(i => i.Rap));
        // recorded days are the real thing
        var snaps = LoadSnapshots(userId);
        foreach (var s in snaps) points[s.Day] = s;

        int dated = groups.Sum(g => g.Copies.Count(c => c != DateTime.MinValue)), all = groups.Sum(g => g.Copies.Count);
        var note = (snaps.Count > 1 ? $"Recorded on this PC for {snaps.Count} days; before that rebuilt" : "Rebuilt")
                 + $" from when they got each item they own now ({dated} of {all} dated) and Roblox's sales."
                 + (groups.Count > withHistory ? $" Their {withHistory} most valuable items follow their RAP over time." : "")
                 + " Items they traded away aren't known, so older days can be lower than they were.";
        return (points.Values.ToList(), note);
    }

    private sealed record Group(long AssetId, bool IsBundle, long Rap, long Value, List<DateTime> Copies);

    /// <summary>
    /// The RAP per day from <paramref name="start"/> on, the way Roblox moves it: each sale pulls it 10 % of the way
    /// towards that sale's price. Scaled so today matches the RAP Roblox shows now.
    /// </summary>
    private static double[] RapSeries(List<RobloxTrades.DaySales> sales, DateTime start, int days, long rapNow)
    {
        var result = new double[days + 1];
        if (sales.Count == 0) { Array.Fill(result, rapNow); return result; }
        double r = sales[0].Price;
        var byDay = sales.ToDictionary(s => s.Day.Date);
        var first = sales[0].Day.Date;
        var from = first < start ? first : start;
        for (var day = from; day <= start.AddDays(days); day = day.AddDays(1))
        {
            if (byDay.TryGetValue(day, out var s)) r = s.Price + (r - s.Price) * Math.Pow(0.9, Math.Min(s.Volume, 200));
            int i = (int)(day - start).TotalDays;
            if (i >= 0) result[i] = day < first ? sales[0].Price : r;
        }
        double last = result[days];
        if (rapNow > 0 && last > 0)
        {
            double k = rapNow / last;
            if (k > 0.33 && k < 3) for (int i = 0; i <= days; i++) result[i] *= k;
        }
        return result;
    }
}
