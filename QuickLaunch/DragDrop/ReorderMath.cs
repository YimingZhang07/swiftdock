namespace QuickLaunch.DragDrop;

public static class ReorderMath
{
    public static int FinalIndex(int oldIndex, int slot, int count)
    {
        if (count < 1 || oldIndex < 0 || oldIndex >= count || slot < 0 || slot > count)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return slot > oldIndex ? slot - 1 : slot;
    }

    public static int Slot(double x, double contentY, int columns, int count, double cell = 88)
    {
        if (columns < 1 || count < 0 || cell <= 0 || !double.IsFinite(x) || !double.IsFinite(contentY))
            throw new ArgumentOutOfRangeException(nameof(columns));
        int col = Math.Clamp((int)Math.Floor(Math.Max(0, x) / cell), 0, columns - 1);
        int row = (int)Math.Floor(Math.Max(0, contentY) / cell);
        int slot = row * columns + col + (x % cell >= cell / 2 ? 1 : 0);
        return Math.Clamp(slot, 0, count);
    }
}
