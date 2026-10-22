using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Swiftdock.ViewModels;

public sealed partial class NameDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    private Swiftdock.Services.UiText _title = "", _error = "";
    public string Title => _title.Value;
    public string Error => _error.Value;
    public NameDialogViewModel() => Swiftdock.Services.Localization.Observe(LanguageChanged);
    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _canEdit = true;
    public string Value => Name.Trim();
    public event Action? Accepted;

    public void Initialize(Swiftdock.Services.UiText title, string value, bool canEdit = true)
    { _title = title; Name = value; CanEdit = canEdit; _error = ""; OnPropertyChanged(string.Empty); }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Accept()
    {
        if (!CanEdit) return;
        if (Value.Length == 0) { _error = L.Text("EnterAName"); OnPropertyChanged(nameof(Error)); }
        else Accepted?.Invoke();
    }
}
