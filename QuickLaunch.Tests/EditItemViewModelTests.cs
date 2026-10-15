using System.IO;
using System.Windows.Input;
using QuickLaunch.Models;
using QuickLaunch.Services;
using QuickLaunch.ViewModels;
using Xunit;

namespace QuickLaunch.Tests;

public sealed class EditItemViewModelTests
{
    [Theory]
    [InlineData(LaunchKind.App, "", "", "", false, "名称和目标不能为空。")]
    [InlineData(LaunchKind.Url, "网站", "example.com", "", false, "请输入有效的 http/https 网址。")]
    [InlineData(LaunchKind.App, "程序", "relative.exe", "", false, "请输入不含引号的绝对路径。")]
    [InlineData(LaunchKind.App, "程序", @"C:\note.txt", "", false, "请选择 .exe、.lnk、.bat 或 .cmd 程序。")]
    [InlineData(LaunchKind.App, "程序", @"C:\tool.exe", "relative", false, "工作目录必须是有效绝对路径。")]
    [InlineData(LaunchKind.File, "文件", @"C:\folder", "", true, "请选择文件，不能选择文件夹。")]
    [InlineData(LaunchKind.Folder, "目录", @"C:\missing", "", false, "找不到该文件夹。")]
    public async Task ValidationMessagesMatchExistingDialog(LaunchKind kind, string name, string path, string working, bool directory, string error)
    {
        var environment = new EditorEnvironment { Facts = (false, directory) };
        using var model = Create(environment: environment);
        model.Initialize(new(name, path, kind, WorkingDirectory: working));
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Null(model.Result); Assert.Equal(error, model.Error); Assert.True(model.IsEditing);
    }

    [Fact]
    public void AutoFillFollowsHostAndLocationsUntilUserTypesName()
    {
        using var model = Create();
        model.Path = "  \"https://github.com/download.exe\"  ";
        Assert.Equal(LaunchKind.Url, model.SelectedKind); Assert.Equal("github.com", model.Name);
        model.Path = "https://example.com/"; Assert.Equal("example.com", model.Name);
        model.SelectLocationCommand.Execute(CommonLocation.Desktop); Assert.Equal("桌面", model.Name);
        model.SelectLocationCommand.Execute(CommonLocation.Downloads); Assert.Equal("下载", model.Name);
        model.Name = "我的目录";
        foreach (var location in Enum.GetValues<CommonLocation>()) model.SelectLocationCommand.Execute(location);
        model.Path = "https://openai.com/";
        Assert.Equal("我的目录", model.Name);
        model.Name = ""; model.Path = "https://github.com/";
        Assert.Equal("github.com", model.Name);
    }

    [Fact]
    public void BrowseUsesSelectedTypeAndPreservesTypedAndExistingNames()
    {
        var picker = new Picker { Target = @"C:\tools\tool.exe" };
        using var model = Create(picker);
        model.BrowseCommand.Execute(null);
        Assert.Equal(LaunchKind.App, picker.Kind); Assert.Equal("tool", model.Name);
        Assert.Equal(ItemIdentity.PortablePath(picker.Target), model.Path);
        picker.Target = @"C:\folder\"; model.SelectedKind = LaunchKind.Folder;
        model.BrowseCommand.Execute(null); Assert.Equal("folder", model.Name);
        Assert.Equal(LaunchKind.Folder, picker.Kind);
        model.Name = "typed"; picker.Target = @"C:\different"; model.BrowseCommand.Execute(null);
        Assert.Equal("typed", model.Name);
        model.Initialize(new("original", "https://example.com/", LaunchKind.Url));
        model.Path = "https://github.com/"; Assert.Equal("original", model.Name);
        picker.Target = null; model.SelectedKind = LaunchKind.File; string before = model.Path;
        model.BrowseCommand.Execute(null); Assert.Equal(before, model.Path);
    }

    [Fact]
    public void AutoFilledNamesRespectTheViewLimitAndLocationErrorsAreStable()
    {
        var picker = new Picker { Target = "C:\\" + new string('a', 200) + ".exe" };
        var environment = new EditorEnvironment { LocationError = true };
        using var model = Create(picker, environment);
        model.BrowseCommand.Execute(null); Assert.Equal(160, model.Name.Length);
        model.SelectLocationCommand.Execute(CommonLocation.Documents);
        Assert.Equal("无法读取该常用位置。", model.Error);
    }

    [Theory]
    [InlineData(@"C:\tool.exe", true, false, LaunchKind.App)]
    [InlineData(@"C:\tool.cmd", true, false, LaunchKind.App)]
    [InlineData(@"C:\note.txt", true, false, LaunchKind.File)]
    [InlineData(@"C:\folder", false, true, LaunchKind.Folder)]
    [InlineData("https://example.com/file.exe", false, false, LaunchKind.Url)]
    public async Task TargetFactsAutoSwitchTypes(string path, bool file, bool directory, LaunchKind expected)
    {
        using var model = Create(environment: new EditorEnvironment { Facts = (file, directory) });
        model.SelectedKind = LaunchKind.File; model.Path = path;
        await model.TargetRefresh;
        Assert.Equal(expected, model.SelectedKind); Assert.Equal(expected, model.PreviewTarget?.Kind);
    }

    [Fact]
    public async Task ManualSelectionIncludingCheckedCardWinsOverPendingInference()
    {
        var pending = new TaskCompletionSource<(bool File, bool Directory)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = new EditorEnvironment { Inspect = (_, _) => { started.TrySetResult(); return pending.Task; } };
        using var model = Create(environment: environment);
        model.Path = @"C:\folder"; var obsolete = model.TargetRefresh;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.SelectTypeCommand.Execute(LaunchKind.App);
        pending.SetResult((false, true)); await obsolete; await model.TargetRefresh;
        Assert.Equal(LaunchKind.App, model.SelectedKind);
        model.Path = "https://example.com/"; model.SelectTypeCommand.Execute(LaunchKind.File);
        await model.TargetRefresh; Assert.Equal(LaunchKind.File, model.SelectedKind);
    }

    [Theory]
    [InlineData(LaunchKind.App, @"C:\tool.exe", true)]
    [InlineData(LaunchKind.App, @"C:\tool.lnk", false)]
    [InlineData(LaunchKind.File, @"C:\note.txt", false)]
    [InlineData(LaunchKind.Folder, @"C:\folder", false)]
    [InlineData(LaunchKind.Url, "https://example.com/", false)]
    public async Task SaveNormalizesAndOnlyRetainsApplicableAdvancedValues(LaunchKind kind, string path, bool arguments)
    {
        using var model = Create(environment: new EditorEnvironment { Facts = (true, kind == LaunchKind.Folder) });
        model.Initialize(new("  name  ", " \"" + path + "\" ", kind, "--test", @" C:\working ", true) { Icon = "glyph:E943:#5B4FD1" });
        bool accepted = false; model.Accepted += () => accepted = true;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.True(accepted); Assert.NotNull(model.Result); Assert.Equal("name", model.Result.Name);
        Assert.Equal(kind, model.Result.Kind); Assert.Equal(kind == LaunchKind.Url ? path : ItemIdentity.PortablePath(path), model.Result.Path);
        Assert.Equal(arguments ? "--test" : "", model.Result.Arguments);
        Assert.Equal(arguments ? @"C:\working" : "", model.Result.WorkingDirectory);
        Assert.Equal(kind == LaunchKind.App, model.Result.RunAsAdmin); Assert.Equal("glyph:E943:#5B4FD1", model.Result.Icon);
        Assert.Equal(kind == LaunchKind.App, model.AdvancedExpanded);
        Assert.Equal(arguments, model.CanSetArguments);
    }

    [Fact]
    public async Task IconChoicesKeepColorAndProduceGlyphFileAndDefaultResults()
    {
        var picker = new Picker { Icon = @"C:\icons\sample.png" };
        using var model = Create(picker);
        model.Initialize(new("icon", "https://example.com/", LaunchKind.Url) { Icon = "glyph:E943:#5B4FD1" });
        Assert.Equal(0xE943, model.SelectedGlyph); Assert.Equal("#5B4FD1", model.IconColor);
        model.SelectGlyphCommand.Execute(0xE753); model.SelectColorCommand.Execute("#C2410C");
        Assert.Equal("glyph:E753:#C2410C", model.CustomIcon);
        model.OpenIconPickerCommand.Execute(null); Assert.True(model.IsIconPickerOpen);
        model.SelectIconFileCommand.Execute(null); Assert.False(model.IsIconPickerOpen);
        Assert.Null(model.SelectedGlyph); Assert.Equal("file:" + ItemIdentity.PortablePath(picker.Icon), model.CustomIcon);
        await model.SaveCommand.ExecuteAsync(null); Assert.Equal(model.CustomIcon, model.Result?.Icon);
        model.ResetIconCommand.Execute(null); Assert.Null(model.CustomIcon);
        await model.SaveCommand.ExecuteAsync(null); Assert.Null(model.Result?.Icon);
    }

    [Fact]
    public async Task ReadOnlyDisablesEveryMutationCommandAndCannotAccept()
    {
        var picker = new Picker { Target = @"C:\tool.exe", Icon = @"C:\icon.png" };
        using var model = Create(picker);
        model.Initialize(new("name", "https://example.com/", LaunchKind.Url), canEdit: false);
        ICommand[] commands = [model.SaveCommand, model.BrowseCommand, model.SelectLocationCommand, model.SelectTypeCommand,
            model.OpenIconPickerCommand, model.SelectGlyphCommand, model.SelectColorCommand, model.SelectIconFileCommand, model.ResetIconCommand];
        Assert.All(commands, command => Assert.False(command.CanExecute(null)));
        await model.SaveCommand.ExecuteAsync(null); model.BrowseCommand.Execute(null); model.SelectIconFileCommand.Execute(null);
        Assert.Null(model.Result); Assert.Equal(0, picker.Calls); Assert.False(model.IsEditing);
        int changes = 0; model.SaveCommand.CanExecuteChanged += (_, _) => changes++;
        model.CanEdit = true; Assert.True(model.SaveCommand.CanExecute(null)); Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ClosingDuringValidationPreventsAcceptanceAndCancelsWork()
    {
        var completion = new TaskCompletionSource<(bool File, bool Directory)>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        using var model = Create(environment: new EditorEnvironment { Inspect = (_, token) => { observed = token; return completion.Task; } });
        model.Initialize(new("name", @"C:\tool.exe", LaunchKind.App));
        bool accepted = false; model.Accepted += () => accepted = true;
        var save = model.SaveCommand.ExecuteAsync(null);
        Assert.True(model.IsSaving); Assert.False(model.SaveCommand.CanExecute(null)); Assert.False(model.BrowseCommand.CanExecute(null));
        model.Dispose(); Assert.True(observed.IsCancellationRequested);
        completion.SetResult((true, false)); await save;
        Assert.Null(model.Result); Assert.False(accepted);
    }

    private static EditItemViewModel Create(Picker? picker = null, EditorEnvironment? environment = null) =>
        new(picker ?? new(), environment ?? new());
    private sealed class Picker : IFilePicker
    {
        internal string? Target { get; set; }
        internal string? Icon { get; set; }
        internal LaunchKind Kind { get; private set; }
        internal int Calls { get; private set; }
        public string? PickTarget(LaunchKind kind) { Kind = kind; Calls++; return Target; }
        public string? PickIcon() { Calls++; return Icon; }
    }
    private sealed class EditorEnvironment : IItemEditorEnvironment
    {
        internal (bool File, bool Directory) Facts { get; set; }
        internal bool LocationError { get; set; }
        internal Func<string, CancellationToken, Task<(bool File, bool Directory)>>? Inspect { get; set; }
        public IReadOnlyList<LibraryGlyph> AvailableGlyphs => IconCatalog.Glyphs;
        public string ResolveLocation(CommonLocation location) => LocationError ? throw new IOException("fixture") : @"C:\locations\" + location;
        public Task<(bool File, bool Directory)> InspectAsync(string path, CancellationToken cancellation) =>
            Inspect?.Invoke(path, cancellation) ?? Task.FromResult(Facts);
    }
}
