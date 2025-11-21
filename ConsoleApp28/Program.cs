using System.Diagnostics;

class Program
{
    static async Task Main(string[] args)
    {
        // Root folder that contains your repositories
        var rootPath = args.Length > 0 ? args[0] : @"C:\Repos";

        // Name of the feature branch to create in each repo
        var featureBranchName = args.Length > 1 ? args[1] : "feature/my-new-branch";

        Console.WriteLine($"Scanning for git repositories under: {rootPath}");
        Console.WriteLine($"Feature branch to create: {featureBranchName}");
        Console.WriteLine();

        var gitRepos = FindGitRepositories(rootPath);

        Console.WriteLine($"Found {gitRepos.Length} repositories.");
        Console.WriteLine();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        await Parallel.ForEachAsync(gitRepos, parallelOptions, async (repo, cancellationToken) =>
        {
            try
            {
                Console.WriteLine($"[{repo}] Processing...");

                await CreateFeatureBranchOnDefaultAsync(repo, featureBranchName);

                Console.WriteLine($"[{repo}] Done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{repo}] ERROR: {ex.Message}");
            }
        });

        Console.WriteLine();
        Console.WriteLine("Finished.");

        if (Debugger.IsAttached)
            Debugger.Break();
    }

    // Re-using your directory walker
    static string[] FindGitRepositories(string searchPath, bool includeSubmodules = false, int maxDepth = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(searchPath) || !Directory.Exists(searchPath))
            return Array.Empty<string>();

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

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static async Task CreateFeatureBranchOnDefaultAsync(string repositoryPath, string featureBranchName)
    {
        // Get first remote + its default branch (e.g. origin / main)
        var (remote, defaultBranch) = await GetDefaultRemoteAndBranchAsync(repositoryPath);

        Console.WriteLine($"[{repositoryPath}] Remote: {remote}, default branch: {defaultBranch}");

        // Make sure we have latest default branch
        await RunGitAsync(repositoryPath, $"fetch {remote} {defaultBranch}");

        var startPoint = $"{remote}/{defaultBranch}"; // e.g. origin/main

        // Check if the feature branch already exists locally
        var exists = await BranchExistsAsync(repositoryPath, featureBranchName);
        if (exists)
        {
            Console.WriteLine($"[{repositoryPath}] Branch '{featureBranchName}' already exists. Skipping creation.");
        }
        else
        {
            // Create the new local branch at remote/defaultBranch
            await RunGitAsync(repositoryPath, $"branch {featureBranchName} {startPoint}");
            Console.WriteLine($"[{repositoryPath}] Created local branch '{featureBranchName}' at {startPoint}.");
        }

        // Push branch to remote (idempotent — creates or updates remote ref)
        await RunGitAsync(repositoryPath, $"push {remote} {featureBranchName}:{featureBranchName}");
        Console.WriteLine($"[{repositoryPath}] Pushed '{featureBranchName}' to {remote}.");
    }

    static async Task<bool> BranchExistsAsync(string repositoryPath, string branchName)
    {
        try
        {
            // rev-parse --verify returns 0 if the ref exists, non-zero otherwise
            await RunGitAsync(repositoryPath, $"rev-parse --verify {branchName}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task<(string Remote, string DefaultBranch)> GetDefaultRemoteAndBranchAsync(string repositoryPath)
    {
        // 1. Get first remote name
        var remotesOutput = await RunGitAsync(repositoryPath, "remote");
        var firstRemote = remotesOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Repository has no remotes.");

        // 2. Get remote HEAD, e.g. "origin/main"
        var remoteHead = await RunGitAsync(
            repositoryPath,
            $"symbolic-ref --short refs/remotes/{firstRemote}/HEAD"
        ); // e.g. "origin/main"

        var parts = remoteHead.Split('/', 2);
        var branchName = parts.Length == 2 ? parts[1] : remoteHead; // "main"

        return (firstRemote, branchName);
    }

    static async Task<string> RunGitAsync(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {arguments} failed in {workingDirectory} (exit {process.ExitCode}): {stderr}");

        return stdout;
    }
}
