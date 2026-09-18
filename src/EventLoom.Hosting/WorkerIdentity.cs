namespace EventLoom.Hosting;

internal static class WorkerIdentity
{
    public static string Create() =>
        $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
}
