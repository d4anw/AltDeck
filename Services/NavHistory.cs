namespace RoDeck.Services;

/// <summary>
/// Browser-style back / forward. States are compared by a key (page, tab, which item is open…), so two steps that show
/// the same thing count as one, and going back never lands on a step that looks exactly like where you are.
/// </summary>
public sealed class NavHistory<T> where T : class
{
    private readonly List<T> _back = new();
    private readonly List<T> _forward = new();
    private readonly Func<T, string> _key;
    private readonly int _max;

    public NavHistory(Func<T, string> key, int max = 60) { _key = key; _max = max; }

    /// <summary>Where you are now, as last recorded.</summary>
    public T? Here { get; private set; }
    public int BackCount => _back.Count;
    public int ForwardCount => _forward.Count;

    /// <summary>
    /// You're somewhere (maybe somewhere new). A different place becomes a new step and clears forward, like a browser;
    /// the same place only refreshes what's stored for it.
    /// </summary>
    public void Record(T now)
    {
        if (Here != null && _key(Here) == _key(now)) { Here = now; return; }
        if (Here != null)
        {
            _back.Add(Here);
            if (_back.Count > _max) _back.RemoveAt(0);
        }
        _forward.Clear();
        Here = now;
    }

    /// <summary>
    /// The step to go back to from <paramref name="current"/> (the real state right now), or null at the start.
    /// Steps that look exactly like the current one are skipped, so back always visibly does something.
    /// </summary>
    public T? Back(T current) => Move(current, _back, _forward);

    public T? Forward(T current) => Move(current, _forward, _back);

    private T? Move(T current, List<T> from, List<T> to)
    {
        Record(current);   // anything not recorded yet (e.g. a click a moment ago) counts first
        var here = Here!;
        while (from.Count > 0)
        {
            var s = from[^1];
            from.RemoveAt(from.Count - 1);
            if (_key(s) == _key(here)) continue;
            to.Add(here);
            Here = s;
            return s;
        }
        return null;
    }

    /// <summary>After going back / forward: what's really on screen now (when something couldn't be shown exactly).</summary>
    public void Settle(T actual) => Here = actual;
}
