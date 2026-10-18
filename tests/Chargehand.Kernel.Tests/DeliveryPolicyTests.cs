using Chargehand.Contracts;

namespace Chargehand.Kernel.Tests;

/// <summary>ADR 0069: the profile's local-only roots win over the request, and one policy answers every guard.</summary>
public class DeliveryPolicyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("delivery-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Repo(string relative)
    {
        var path = Directory.CreateDirectory(Path.Combine(_dir, relative)).FullName;
        return path;
    }

    [Fact]
    public void A_repository_under_a_root_is_forced_and_one_beside_it_is_not()
    {
        var policy = new DeliveryPolicy([Repo("work")]);

        Assert.True(policy.Forced(Repo("work/api")));
        Assert.True(policy.Forced(Repo("work/group/api")));
        Assert.False(policy.Forced(Repo("work-other/api")));
        Assert.False(policy.Forced(Repo("personal/api")));
    }

    [Fact]
    public void A_symbolic_link_out_of_a_root_does_not_escape_it()
    {
        var outside = Repo("personal/api");
        var link = Path.Combine(Repo("work"), "link");
        Directory.CreateSymbolicLink(link, outside);

        // The link lives under the root but points outside it: git prints the real path, which is not under the root.
        Assert.False(new DeliveryPolicy([Path.Combine(_dir, "work")]).Forced(PathScope.RealPath(link)));
        // A root given through a link still matches the real directory.
        var rootLink = Path.Combine(_dir, "root-link");
        Directory.CreateSymbolicLink(rootLink, Repo("private"));
        Assert.True(new DeliveryPolicy([rootLink]).Forced(Repo("private/api")));
    }

    [Fact]
    public void The_root_slash_makes_every_repository_local_only()
    {
        var policy = new DeliveryPolicy(["/"]);

        Assert.True(policy.Forced(Repo("anything")));
        Assert.True(policy.IsLocal(null, "/"));
    }

    [Fact]
    public void A_home_relative_root_expands_to_the_home_directory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(new DeliveryPolicy(["~/work"]).Forced(Path.Combine(home, "work", "api")));
    }

    [Fact]
    public void The_request_chooses_local_where_the_profile_forces_nothing()
    {
        var path = Repo("personal/api");

        Assert.False(DeliveryPolicy.None.IsLocal(null, path));
        Assert.False(DeliveryPolicy.None.IsLocal("push", path));
        Assert.True(DeliveryPolicy.None.IsLocal("local", path));
    }

    [Fact]
    public void A_forced_repository_is_local_whatever_is_left_out_and_refuses_a_push_request()
    {
        var policy = new DeliveryPolicy([Repo("work")]);
        var path = Repo("work/api");

        Assert.True(policy.IsLocal(null, path));
        Assert.True(policy.IsLocal("local", path));
        var refused = Assert.Throws<ChargehandException>(() => policy.IsLocal("push", path));
        Assert.Equal(ErrorCode.LocalOnly, refused.Code);
        Assert.Contains("local branch", refused.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_value_is_an_invalid_request()
    {
        var refused = Assert.Throws<ChargehandException>(() => DeliveryPolicy.None.IsLocal("both", Repo("x")));

        Assert.Equal(ErrorCode.InvalidRequest, refused.Code);
    }
}
