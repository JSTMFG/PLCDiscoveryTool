using System.Diagnostics;

namespace Jst.EchoAgent;

internal static class AgentLog
{
    public static void Write(string message, bool error = false)
    {
        try { EventLog.WriteEntry("Application", "JST Echo Reset Agent: " + message, error ? EventLogEntryType.Error : EventLogEntryType.Information); }
        catch { Console.WriteLine(message); }
    }
}
