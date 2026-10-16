using Artemis.Core.Logging;

namespace Artemis.Core.Tests;

public class AppLogTests
{
    // The log is one process-wide file, so these tests must not run beside each other.
    [CollectionDefinition("log", DisableParallelization = true)]
    public sealed class LogCollection;

    [Collection("log")]
    public sealed class Writing
    {
        [Fact]
        public void Lines_carry_a_time_and_a_level_and_an_error_carries_its_details()
        {
            using var t = new TempDir();
            var path = Path.Combine(t.Path, "logs", "artemis.log");
            AppLog.Start(path);
            AppLog.Warn("careful");
            AppLog.Error("it broke", new InvalidOperationException("because"));

            var lines = File.ReadAllLines(path);
            Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d info  Artemis started$", lines[0]);
            Assert.Matches(@"^\S+ \S+ warn  careful$", lines[1]);
            Assert.Matches(@"^\S+ \S+ error it broke$", lines[2]);
            Assert.Contains(lines, l => l.StartsWith("    ") && l.Contains("InvalidOperationException: because"));
        }

        [Fact]
        public void A_log_that_has_grown_too_big_is_set_aside_when_the_next_run_starts()
        {
            using var t = new TempDir();
            var path = t.File("artemis.log", new string('x', (int)AppLog.MaxBytes + 1));
            AppLog.Start(path);

            Assert.True(File.Exists(path + ".old"));
            Assert.Single(File.ReadAllLines(path));
        }

        [Fact]
        public void A_log_that_cannot_be_written_never_throws()
        {
            using var t = new TempDir();
            AppLog.Start(Path.Combine(t.Path, "artemis.log"));
            Directory.Delete(t.Path, recursive: true);
            Directory.CreateDirectory(Path.Combine(t.Path, "artemis.log")); // a folder where the file should be
            AppLog.Info("nowhere to go");
        }
    }
}
