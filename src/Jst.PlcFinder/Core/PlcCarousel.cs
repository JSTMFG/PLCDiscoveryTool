namespace Jst.PlcFinder.Core;

public sealed class PlcCarousel
{
    public PlcRow? Current { get; private set; }
    public int Index { get; private set; } = -1;
    public int Count { get; private set; }

    public void Update(IEnumerable<PlcRow> detected, bool advance = false, IEnumerable<string>? hiddenKeys = null, int offset = 0)
    {
        var hidden = new HashSet<string>(hiddenKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var rows = detected.Where(r => r.Identity.IsController || r.IsEcho || r.Identity.IsBridge && r.Route.Length > 0)
            .Where(r => !hidden.Contains(r.Key))
            .OrderBy(r => r.IpSort).ThenBy(r => r.Route, StringComparer.Ordinal).ToArray();
        Count = rows.Length;
        if (Count == 0) { Current = null; Index = -1; return; }
        int previousIndex = Array.FindIndex(rows, r => r.Key == Current?.Key);
        int start = previousIndex < 0 ? (Index < 0 ? 0 : Index % Count) : previousIndex;
        int movement = offset + (advance && previousIndex >= 0 ? 1 : 0);
        Index = (int)(((long)start + movement) % Count + Count) % Count;
        Current = rows[Index];
    }
}
