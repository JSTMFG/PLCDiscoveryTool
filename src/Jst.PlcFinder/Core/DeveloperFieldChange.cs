namespace Jst.PlcFinder.Core;

public sealed record DeveloperFieldChange(string Label, string Before, string After)
{
    public string Description => $"{Label}: {Display(Before)} → {Display(After)}";

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "(blank)" : value;
}

public static class DeveloperChangeNotice
{
    public static string Format(IEnumerable<(PlcRow Row, IReadOnlyList<DeveloperFieldChange> Changes)> changedRows) =>
        string.Join("\n", changedRows.OrderBy(entry => entry.Row.IpSort).ThenBy(entry => entry.Row.Route)
            .SelectMany(entry => new[] { entry.Row.IpDisplay }.Concat(entry.Changes.Select(change => "  " + change.Description))));
}
