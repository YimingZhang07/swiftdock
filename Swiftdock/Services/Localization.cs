using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Swiftdock.Services;

// ResourceManager uses neutral English resources and the standard zh-CN satellite.
public sealed class Localization : ObservableObject
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    public static ResourceManager Resources { get; } = new("Swiftdock.Resources.Strings", typeof(Localization).Assembly);
    public static Localization Instance { get; } = new();
    public CultureInfo Culture { get; private set; } = Resolve(null, SystemCulture);
    public string this[string key] => Resources.GetString(key, Culture)
        ?? throw new MissingManifestResourceException($"Missing UI resource: {key}");
    public static string? Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    { "zh-cn" => "zh-CN", "en" => "en", _ => null };
    public static CultureInfo Resolve(string? language, CultureInfo system) => CultureInfo.GetCultureInfo(
        Normalize(language) ?? (system.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en"));
    public void Apply(string? language)
    {
        var culture = Resolve(language, SystemCulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (Culture.Name == culture.Name) return;
        Culture = culture;
        OnPropertyChanged(nameof(Culture));
        OnPropertyChanged("Item[]");
    }
    public static UiText Text(string key, params object?[] args) => new(key, args);
    internal static string DefaultGroupName(string? language) => Resources.GetString("Favorites", Resolve(language, SystemCulture))!;
    public static void Observe(EventHandler<PropertyChangedEventArgs> handler) =>
        PropertyChangedEventManager.AddHandler(Instance, handler, nameof(Culture));
    public static void Unobserve(EventHandler<PropertyChangedEventArgs> handler) =>
        PropertyChangedEventManager.RemoveHandler(Instance, handler, nameof(Culture));
    public static T Exception<T>(UiText text) where T : Exception
    {
        var error = (T)Activator.CreateInstance(typeof(T), text.Value)!;
        error.Data[typeof(UiText)] = text;
        return error;
    }
    public static UiText Error(Exception error) => error is FutureConfigException future
        ? Text("ConfigurationVersion0IsNewerThanSupportedVersion1OverwritingIsDis", future.Version)
        : error.Data[typeof(UiText)] as UiText ?? Text("OperationFailed", error.HResult);
    public static string Diagnostic(Exception error) => error.Data[typeof(UiText)] is UiText text ? text.English
        : error is FutureConfigException ? Error(error).English : error.Message;
    public static void Bind(DependencyObject target, DependencyProperty property, UiText text) =>
        BindingOperations.SetBinding(target, property, new Binding(nameof(UiText.Value))
        { Source = text, Mode = BindingMode.OneWay });
    internal static void LocalizeChrome(Window window)
    {
        window.Loaded += (_, _) =>
        {
            foreach (var button in Views.LauncherWindow.Descendants<Wpf.Ui.Controls.TitleBarButton>(window))
            {
                string key = button.ButtonType.ToString();
                if (key is not ("Close" or "Minimize" or "Maximize" or "Restore" or "Help")) continue;
                Bind(button, FrameworkElement.ToolTipProperty, Text(key));
                Bind(button, System.Windows.Automation.AutomationProperties.NameProperty, Text(key));
            }
        };
    }
}

// Retain resource identity and arguments for messages already on screen. User data
// uses literal text and is never translated or rewritten when language changes.
public sealed class UiText(string? key, object?[] arguments, string? separator = null) : INotifyPropertyChanged
{
    public string? Key { get; } = key;
    public object?[] Arguments { get; } = arguments;
    private PropertyChangedEventHandler? _changed;
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { if (_changed is null) Localization.Observe(LanguageChanged); _changed += value; }
        remove { _changed -= value; if (_changed is null) Localization.Unobserve(LanguageChanged); }
    }
    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => _changed?.Invoke(this, new(nameof(Value)));
    public string Value => Format(Localization.Instance.Culture);
    public string English => Format(CultureInfo.GetCultureInfo("en"));
    public static UiText Join(IEnumerable<UiText> texts, string separator = "; ") => new(null, texts.Cast<object?>().ToArray(), separator);
    private string Format(CultureInfo culture) => separator is not null ? string.Join(separator, Arguments.Cast<UiText>().Select(t => t.Format(culture)))
        : Key is null ? (string)Arguments[0]! : string.Format(culture,
        Localization.Resources.GetString(Key, culture) ?? throw new MissingManifestResourceException(Key),
        Arguments.Select(a => a is UiText text ? text.Format(culture) : a).ToArray());
    public static implicit operator UiText(string value) => new(null, [value]);
    public static implicit operator string(UiText value) => value.Value;
    public override string ToString() => Value;
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding($"[{key}]")
    { Source = Localization.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

internal sealed class LocalizedMenuItem : MenuItem
{
    public LocalizedMenuItem(UiText text)
    {
        Localization.Bind(this, HeaderProperty, text);
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, text.Key ?? "MenuItem");
    }
}
