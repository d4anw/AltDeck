using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Models;

public partial class GroupChip : ObservableObject
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    [ObservableProperty] private bool isActive;
}
