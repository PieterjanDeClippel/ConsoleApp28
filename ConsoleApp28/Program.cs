using System.Diagnostics;

var allRepos = FindGitRepositories(@"C:\Repos");
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