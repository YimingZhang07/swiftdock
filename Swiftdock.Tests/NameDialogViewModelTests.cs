using Swiftdock.ViewModels;
using Xunit;

namespace Swiftdock.Tests;

public sealed class NameDialogViewModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void EmptyNameKeepsDialogOpen(string name)
    {
        var model = new NameDialogViewModel(); model.Initialize("新建分组", name);
        bool accepted = false; model.Accepted += () => accepted = true;
        model.AcceptCommand.Execute(null);
        Assert.False(accepted); Assert.Equal("请输入名称。", model.Error);
    }
    [Fact]
    public void AcceptTrimsValueAndReadOnlyDisablesCommand()
    {
        var model = new NameDialogViewModel(); model.Initialize("重命名分组", "  新名称  ");
        int accepts = 0, changes = 0; model.Accepted += () => accepts++;
        model.AcceptCommand.CanExecuteChanged += (_, _) => changes++;
        model.AcceptCommand.Execute(null);
        Assert.Equal("重命名分组", model.Title); Assert.Equal("新名称", model.Value); Assert.Equal(1, accepts);
        model.CanEdit = false;
        Assert.False(model.AcceptCommand.CanExecute(null)); model.AcceptCommand.Execute(null);
        Assert.Equal(1, accepts); Assert.Equal(1, changes);
    }
}
