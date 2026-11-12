namespace Jellyfin.Plugin.SceneXray.Tasks;

/// <summary>Progress arithmetic for the scheduled tasks (no Jellyfin types, so it is testable).</summary>
public static class TaskProgress
{
    /// <summary>
    /// Percent done: finished titles plus the running titles' fractions (0-1 each, summed), out of
    /// the batch.
    /// </summary>
    public static double Batch(int finished, double runningFraction, int total) =>
        total == 0 ? 100 : Math.Clamp(100.0 * (finished + Math.Max(runningFraction, 0)) / total, 0, 100);
}
