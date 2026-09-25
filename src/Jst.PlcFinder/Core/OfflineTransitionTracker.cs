namespace Jst.PlcFinder.Core;

public sealed class OfflineTransitionTracker
{
    private readonly HashSet<string> previouslyOnline = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> notifiedOffline = new(StringComparer.OrdinalIgnoreCase);

    public bool Observe(string key, string status)
    {
        if (status == "Online")
        {
            previouslyOnline.Add(key);
            notifiedOffline.Remove(key);
            return false;
        }
        if (status != "Not responding" || !previouslyOnline.Contains(key)) return false;
        return notifiedOffline.Add(key);
    }

    public void Clear()
    {
        previouslyOnline.Clear();
        notifiedOffline.Clear();
    }
}
