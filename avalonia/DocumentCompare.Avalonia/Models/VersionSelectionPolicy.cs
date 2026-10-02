namespace DocumentCompare.Avalonia.Models;

public static class VersionSelectionPolicy
{
    public static (int Before, int After, bool Changed) Toggle(int before, int after, int index)
    {
        if (before == index) return (-1, after, true);
        if (after == index) return (before, -1, true);
        if (before < 0) return (index, after, true);
        if (after < 0) return (before, index, true);
        return (before, after, false);
    }
}
