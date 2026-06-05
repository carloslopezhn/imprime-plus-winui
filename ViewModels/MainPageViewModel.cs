using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImprimePlus.ViewModels;

/// <summary>
/// Sample ViewModel using CommunityToolkit.Mvvm partial property syntax.
/// Uses <see cref="ObservableProperty"/> for change notification and
/// <see cref="RelayCommand"/> for command binding.
/// </summary>
public partial class MainPageViewModel : ObservableObject
{
    [ObservableProperty]
    private string greeting = "Hello, WinUI!";

    [ObservableProperty]
    private int counter;

    [RelayCommand]
    private void Increment()
    {
        Counter++;
    }

    [RelayCommand]
    private void Decrement()
    {
        Counter--;
    }
}
