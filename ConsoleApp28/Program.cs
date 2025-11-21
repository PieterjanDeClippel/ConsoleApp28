using System.Diagnostics;

var gitRepos = FindGitRepositories(@"C:\Repos");
var reposAndBranches = await Task.WhenAll(gitRepos.Select(async repo => new
{
    Repository = repo,
    DefaultBranch = await GetDefaultBranchNameAsync(repo),
}));
Debugger.Break();


static string[] FindGitRepositories(string searchPath, bool includeSubmodules = false, int maxDepth = int.MaxValue)
{
    if (string.IsNullOrWhiteSpace(searchPath) || !Directory.Exists(searchPath))
        return [];

    var results = new List<string>();
    var stack = new Stack<(string path, int depth)>();
    stack.Push((searchPath, 0));

    while (stack.Count > 0)
    {
        var (current, depth) = stack.Pop();
        if (depth > maxDepth) continue;

        var gitDir = Path.Combine(current, ".git");
        if (Directory.Exists(gitDir))
        {
            results.Add(current);

            // Skip traversing deeper once a repository root is found
            if (!includeSubmodules) continue;
        }

        if (depth == maxDepth) continue;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push((dir, depth + 1));
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    return results.Distinct().ToArray();
}

static async Task<string> GetDefaultBranchNameAsync(string repositoryPath)
{
    var process = Process.Start(new ProcessStartInfo
    {
        FileName = "powershell",
        ArgumentList = {
            "-NoProfile",
            "-Command",
            "git symbolic-ref --short \"refs/remotes/$(git remote | Select-Object -First 1)/HEAD\""
        },
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = repositoryPath,
    }) ?? throw new InvalidOperationException("Could not spawn powershell");

    await process.WaitForExitAsync();
    string output = process!.StandardOutput.ReadToEnd().Trim();
    string error = process.StandardError.ReadToEnd().Trim();

    return output;
}
