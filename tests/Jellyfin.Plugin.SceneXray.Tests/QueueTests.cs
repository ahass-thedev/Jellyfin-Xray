using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Services;
using Jellyfin.Plugin.SceneXray.Tasks;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

public class QueueTests
{
    [Fact]
    public void NewMediaRunsAfterViewersAndBeforeTheBacklog()
    {
        Assert.True(QueuePriority.Playback < QueuePriority.NewMedia);
        Assert.True(QueuePriority.Manual < QueuePriority.NewMedia);
        Assert.True(QueuePriority.NewMedia < QueuePriority.Library);
    }

    [Fact]
    public void TaskProgressMovesDuringATitle()
    {
        // 2,800 titles pending: finished titles alone would show 0% for the first ~28.
        Assert.Equal(0, TaskProgress.Batch(0, 0, 2800));
        Assert.True(TaskProgress.Batch(0, 0.5, 2800) > 0);
        Assert.Equal(50, TaskProgress.Batch(1, 0, 2));
        Assert.Equal(75, TaskProgress.Batch(1, 0.5, 2));
    }

    [Fact]
    public void TaskProgressStaysInRange()
    {
        Assert.Equal(100, TaskProgress.Batch(0, 0, 0));
        Assert.Equal(100, TaskProgress.Batch(2, 1, 2));
        Assert.Equal(0, TaskProgress.Batch(0, -1, 4));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(9, 4)]
    public void SlotsStayInRange(int setting, int expected)
    {
        Assert.Equal(expected, AnalysisQueue.Slots(new PluginConfiguration { MaxConcurrentAnalyses = setting }));
    }

    [Fact]
    public void AFreeSlotMeansNothingIsPaused()
    {
        Assert.Null(Victim(2, new R(QueuePriority.Library, 1)));
    }

    [Fact]
    public void TheBacklogIsPausedBeforeNewMedia()
    {
        var backlog = new R(QueuePriority.Library, 1);
        Assert.Same(backlog, Victim(2, new R(QueuePriority.NewMedia, 2), backlog));
    }

    [Fact]
    public void TheMostRecentlyStartedIsPausedFirst()
    {
        var newer = new R(QueuePriority.Library, 5);
        Assert.Same(newer, Victim(2, new R(QueuePriority.Library, 1), newer));
    }

    [Fact]
    public void ViewersAndAdminsAreNeverPaused()
    {
        Assert.Null(Victim(2, new R(QueuePriority.Playback, 1), new R(QueuePriority.Manual, 2)));
    }

    [Fact]
    public void TaskProgressCountsSeveralRunningTitles()
    {
        Assert.Equal(50, TaskProgress.Batch(1, 1, 4)); // 1 done, two halfway
    }

    private static R? Victim(int slots, params R[] running) => AnalysisQueue.Victim(running, slots, r => r.P, r => r.Started);

    private sealed record R(QueuePriority P, long Started);
}
