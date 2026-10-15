using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace QuickLaunch.ViewModels;

public sealed partial class NameDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "", _name = "", _error = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _canEdit = true;
    public string Value => Name.Trim();
    public event Action? Accepted;

    public void Initialize(string title, string value, bool canEdit = true)
    { Title = title; Name = value; CanEdit = canEdit; Error = ""; }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Accept()
    {
        if (!CanEdit) return;
        if (Value.Length == 0) Error = "请输入名称。";
        else Accepted?.Invoke();
    }
}
