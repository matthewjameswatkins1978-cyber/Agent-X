using System.Text.RegularExpressions;

namespace AgentX.Core.Tests;

/// <summary>
/// D1 packaging groundwork: the portable distribution's third-party
/// notices must name every direct NuGet dependency we ship. If a
/// PackageReference is added without a notices entry, this fails.
/// </summary>
public sealed class PackagingTests
{
    private const string PackagePattern =
        @"<PackageReference\s+Include=""([^""]+)""\s+Version=""([^""]+)""";

    [Fact]
    public void ThirdPartyNotices_CoverEveryDirectPackageReference()
    {
        var repo = FindRepoRoot();
        var notices = File.ReadAllText(Path.Combine(repo, "packaging", "THIRD-PARTY-NOTICES.txt"));
        var missing = new List<string>();
        foreach (var csproj in Directory.GetFiles(Path.Combine(repo, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);
            foreach (Match match in Regex.Matches(text, PackagePattern))
            {
                var id = match.Groups[1].Value;
                var version = match.Groups[2].Value;
                if (!notices.Contains(id, StringComparison.OrdinalIgnoreCase)
                    || !notices.Contains(version, StringComparison.Ordinal))
                {
                    missing.Add($"{id} {version} ({Path.GetFileName(csproj)})");
                }
            }
        }

        Assert.True(missing.Count == 0,
            "packaging/THIRD-PARTY-NOTICES.txt must name each shipped package: " + string.Join("; ", missing));
    }

    [Fact]
    public void FontLicenceTexts_AccompanyTheBundledFonts()
    {
        var repo = FindRepoRoot();
        var geist = Path.Combine(repo, "src", "AgentX.App", "Assets", "Fonts", "OFL-Geist.txt");
        var mona = Path.Combine(repo, "src", "AgentX.App", "Assets", "Fonts", "OFL-Monaspace.txt");
        Assert.True(File.Exists(geist), "OFL-Geist.txt must sit beside the bundled fonts.");
        Assert.True(File.Exists(mona), "OFL-Monaspace.txt must sit beside the bundled fonts.");
        // Complete texts, not stubs: exact upstream byte counts.
        Assert.Equal(4383, new FileInfo(geist).Length);
        Assert.Equal(4479, new FileInfo(mona).Length);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", File.ReadAllText(geist));
        Assert.Contains("Copyright 2024 The Geist Project Authors", File.ReadAllText(geist));
        Assert.Contains("Reserved Font Name \"Monaspace\"", File.ReadAllText(mona));
        // Notices pointers resolve to the real committed paths.
        var notices = File.ReadAllText(Path.Combine(repo, "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("src/AgentX.App/Assets/Fonts/OFL-Geist.txt", notices);
        Assert.Contains("src/AgentX.App/Assets/Fonts/OFL-Monaspace.txt", notices);
    }

    [Fact]
    public void PortableDistribution_CarriesTheFontLicences()
    {
        var repo = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(repo, "scripts", "New-PortableDist.ps1"));
        Assert.Contains("OFL-Geist.txt", script);
        Assert.Contains("OFL-Monaspace.txt", script);
        var distNotices = File.ReadAllText(Path.Combine(repo, "packaging", "THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("OFL-Geist.txt", distNotices);
        Assert.Contains("OFL-Monaspace.txt", distNotices);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentX.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate AgentX.sln above the test binary.");
        return dir.FullName;
    }
}
