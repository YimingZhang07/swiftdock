namespace QuickLaunch.Services;

public static class IconSizePresets
{
    public static int Step(int size, int direction) => direction switch
    {
        > 0 => size < 32 ? 32 : size < 40 ? 40 : 48,
        < 0 => size > 48 ? 48 : size > 40 ? 40 : 32,
        _ => size
    };
}
