namespace RoDeck.Models;

/// <summary>Where one account's client window sits (screen pixels).</summary>
public class WindowSlot
{
    public string AccountId { get; set; } = "";
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

/// <summary>A saved arrangement of client windows, matched back to clients by account.</summary>
public class LayoutProfile
{
    public string Name { get; set; } = "";
    public List<WindowSlot> Slots { get; set; } = new();
}
