using Artemis.Core.Achievements;

namespace Artemis.Core.Tests;

public class AchievementVerdictTests
{
    [Fact]
    public void A_mod_with_no_covered_file_and_no_counting_plugin_leaves_achievements_on()
    {
        var v = AchievementCheck.Judge([], pluginCounts: false);
        Assert.Equal(AchievementState.On, v.State);
        Assert.Contains("stay on", v.Tip);
    }

    [Fact]
    public void A_mod_that_has_not_been_looked_at_is_unknown_unless_its_plugin_already_decides()
    {
        Assert.Equal(new AchievementVerdict(AchievementState.Unknown, ""), AchievementCheck.Judge(null, pluginCounts: false));
        Assert.Equal(AchievementState.Off, AchievementCheck.Judge(null, pluginCounts: true).State);
    }

    [Fact]
    public void Covered_files_turn_achievements_off_and_the_tip_counts_them()
    {
        var one = AchievementCheck.Judge(["events/e.txt"], false);
        Assert.Equal(AchievementState.Off, one.State);
        Assert.Equal("Adds or changes 1 file the game's checksum covers, such as events/e.txt. Achievements turn off.", one.Tip);
        Assert.StartsWith("Adds or changes 2 files", AchievementCheck.Judge(["a.txt", "b.txt"], false).Tip);
    }

    [Fact]
    public void A_counting_plugin_turns_achievements_off_by_itself_and_the_tip_says_why()
    {
        var v = AchievementCheck.Judge([], pluginCounts: true);
        Assert.Equal(AchievementState.Off, v.State);
        Assert.Equal("Its plugin counts toward the game's checksum while it is switched on. Achievements turn off.", v.Tip);
    }

    [Fact]
    public void Files_and_a_plugin_are_both_named()
    {
        var v = AchievementCheck.Judge(["common/a.txt"], pluginCounts: true);
        Assert.Equal(AchievementState.Off, v.State);
        Assert.Contains("such as common/a.txt, and its plugin counts", v.Tip);
    }
}
