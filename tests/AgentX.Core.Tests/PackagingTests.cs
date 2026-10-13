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
