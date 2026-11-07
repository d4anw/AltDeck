using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AltDeck.Controls;

public enum LoadState { Ready, Loading, Empty, Error }

/// <summary>
/// Wraps a page's content and shows "Loading…", an empty message or an error with a Try again button instead,
/// depending on <see cref="State"/>. Its look is the StateView style in App.xaml.
/// </summary>
public class StateView : ContentControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(LoadState), typeof(StateView), new PropertyMetadata(LoadState.Ready));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(StateView), new PropertyMetadata(""));
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StateView), new PropertyMetadata("Nothing here yet."));
    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(StateView), new PropertyMetadata(null));

    public LoadState State { get => (LoadState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    /// <summary>The error text (Error) or what's loading (Loading).</summary>
    public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }
    public ICommand? RetryCommand { get => (ICommand?)GetValue(RetryCommandProperty); set => SetValue(RetryCommandProperty, value); }
}

/// <summary>
/// The state of one thing a page loads. Bind a StateView to State / Message, call RunAsync with the loading work.
/// A new RunAsync cancels the previous one, so switching accounts or searching again never shows old results.
/// </summary>
public partial class Loader : ObservableObject
{
    [ObservableProperty] private LoadState state = LoadState.Ready;
    [ObservableProperty] private string message = "";
    private CancellationTokenSource? _cts;

    /// <summary>Runs <paramref name="work"/>; it returns false when there's nothing to show (Empty).</summary>
    public async Task RunAsync(Func<CancellationToken, Task<bool>> work, string loadingText = "Loading…")
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        State = LoadState.Loading;
        Message = loadingText;
        try
        {
            bool any = await work(cts.Token);
            if (cts.IsCancellationRequested) return;
            State = any ? LoadState.Ready : LoadState.Empty;
            Message = "";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            State = LoadState.Error;
            Message = ex is AltDeck.Services.RobloxException ? ex.Message : "Something went wrong: " + ex.Message;
        }
    }

    public void Cancel() => _cts?.Cancel();
}
