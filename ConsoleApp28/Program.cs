using System.Diagnostics;

var allRepos = FindGitRepositories(@"C:\Repos", 2);
Debugger.Break();

static string[] FindGitRepositories(string searchPath, int maxDepth = int.MaxValue)
{
    var o = new EnumerationOptions();

    var gitRepos = new List<string>();
    var directories = Directory.GetDirectories(searchPath, ".git", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = maxDepth, AttributesToSkip = FileAttributes.Normal | FileAttributes.System | FileAttributes.Temporary })
        .Select(p => p.EndsWith(".git") ? p.Substring(0, p.Length - 4) : p)
        .ToArray();

    return directories;
}