using Chargehand.Runtime;

namespace Chargehand.Tests;

public sealed class WorkerKillTests : IDisposable
{
    private readonly TempDir _cgroup = new();

    public void Dispose() => _cgroup.Dispose();

    [Fact]
    public void Memory_reads_the_peak_the_limit_and_the_kills_from_the_cgroup()
    {
        _cgroup.Write("memory.peak", "2448293888\n");
        _cgroup.Write("memory.max", "2684354560\n");
        _cgroup.Write("memory.events", "low 0\nhigh 0\nmax 12\noom 1\noom_kill 1\n");
        Assert.Equal("container memory peak 2335 MB of 2560 MB, 1 out-of-memory kill", WorkerKill.Memory(_cgroup.Path));
    }

    [Fact]
    public void Memory_leaves_out_an_unlimited_limit_and_no_kills()
    {
        _cgroup.Write("memory.peak", "1048576");
        _cgroup.Write("memory.max", "max");
        _cgroup.Write("memory.events", "oom_kill 0");
        Assert.Equal("container memory peak 1 MB", WorkerKill.Memory(_cgroup.Path));
    }

    [Fact]
    public void Memory_is_null_without_a_cgroup() => Assert.Null(WorkerKill.Memory(Path.Combine(_cgroup.Path, "none")));
}
