using DataBridge.Core.Detection;

namespace DataBridge.Tests;

public class FirefoxTests
{
    private const string Root = @"C:\Users\X\AppData\Roaming\Mozilla\Firefox";

    [Fact]
    public void Install_default_wins_over_legacy_default_flag()
    {
        // Same layout as a real machine: legacy "default" has Default=1, the install uses default-release.
        const string profiles = """
            [Profile1]
            Name=default
            IsRelative=1
            Path=Profiles/hpafvwfy.default
            Default=1

            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/m53emrtd.default-release

            [Install308046B0AF4A39CB]
            Default=Profiles/m53emrtd.default-release
            Locked=1
            """;
        const string installs = "[308046B0AF4A39CB]\nDefault=Profiles/m53emrtd.default-release\nLocked=1\n";

        var r = FirefoxProfiles.Resolve(Root, profiles, installs, _ => true);

        Assert.Equal(2, r.Profiles.Count);
        Assert.Equal([Root + @"\Profiles\m53emrtd.default-release"], r.ActiveProfilePaths);
        Assert.Null(r.Warning);
    }

    [Fact]
    public void Several_profiles_without_any_hint_are_not_chosen_arbitrarily()
    {
        const string profiles = """
            [Profile0]
            Name=a
            IsRelative=1
            Path=Profiles/a

            [Profile1]
            Name=b
            IsRelative=1
            Path=Profiles/b
            """;

        var r = FirefoxProfiles.Resolve(Root, profiles, null, _ => true);

        Assert.Empty(r.ActiveProfilePaths);
        Assert.NotNull(r.Warning);
    }

    [Fact]
    public void Single_profile_is_active()
    {
        var r = FirefoxProfiles.Resolve(Root, "[Profile0]\nName=only\nIsRelative=1\nPath=Profiles/only\n", null, _ => true);
        Assert.Single(r.ActiveProfilePaths);
    }
}
