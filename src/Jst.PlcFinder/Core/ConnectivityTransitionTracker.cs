namespace Jst.PlcFinder.Core;

public enum ConnectivityTransition { None, StoppedResponding, RespondingAgain }

public sealed class ConnectivityTransitionTracker
{
    private readonly HashSet<string> hasResponded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> notResponding = new(StringComparer.OrdinalIgnoreCase);

    public ConnectivityTransition Observe(string key, string status)
    {
        if (status == "Not responding")
            return hasResponded.Contains(key) && notResponding.Add(key)
                ? ConnectivityTransition.StoppedResponding : ConnectivityTransition.None;

        // These statuses all require a current reply from the controller. Missing tags
        // are a separate problem and should not hide the restored network connection.
        if (status is not ("Online" or "Partial" or "Station tags not found" or "Tag read timeout" or "Tag read failed"))
            return ConnectivityTransition.None;

        hasResponded.Add(key);
        return notResponding.Remove(key) ? ConnectivityTransition.RespondingAgain : ConnectivityTransition.None;
    }
}
