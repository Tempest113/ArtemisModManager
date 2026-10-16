using System.Diagnostics;
using System.IO;

namespace Artemis.App.Services;

/// <summary>With ARTEMIS_TIMING set to a file name, appends how long after the process started each step happened. Does nothing otherwise.</summary>
public static class Timing
{
    private static readonly string? Log = Environment.GetEnvironmentVariable("ARTEMIS_TIMING");

    public static void Mark(string step)
    {
        if (Log is null) return;
        using var self = Process.GetCurrentProcess();
        File.AppendAllText(Log, $"{(DateTime.Now - self.StartTime).TotalMilliseconds,6:0} ms  {step}\n");
    }
}
