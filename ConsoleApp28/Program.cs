using System.Diagnostics;

class Program
{
    static async Task Main(string[] args)
    {
        // Root folder: default is current working directory
        var rootPath = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        // Name of the feature branch to create
        var featureBranchName = args.Length > 1 ? args[1] : "feature/my-new-branch";

        Console.WriteLine($"Root path: {rootPath}");
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
                await CreateFeatureBranchFromDefaultAsync(repo, featureBranchName);
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

    // Your repo finder, slightly cleaned up
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

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static async Task CreateFeatureBranchFromDefaultAsync(string repositoryPath, string featureBranchName)
    {
        // 1. Determine first remote + its default branch
        var (remote, defaultBranch) = await GetDefaultRemoteAndBranchAsync(repositoryPath);
        Console.WriteLine($"[{repositoryPath}] Remote: {remote}, default branch: {defaultBranch}");

        // 2. Fetch latest default branch
        await RunGitAsync(repositoryPath, $"fetch {remote} {defaultBranch}");

        // 3. Stash outstanding changes, if any
        var hasChanges = await HasUncommittedChangesAsync(repositoryPath);
        var createdStash = false;

        if (hasChanges)
        {
            Console.WriteLine($"[{repositoryPath}] Uncommitted changes detected. Stashing...");
            // No spaces in message to avoid quoting fun
            await RunGitAsync(repositoryPath, $"stash push -u -m auto-stash-for-{featureBranchName}");
            createdStash = true;
        }

        // 4. Switch to default branch (auto-creates from remote if needed on modern git)
        Console.WriteLine($"[{repositoryPath}] Switching to default branch '{defaultBranch}'...");
        await RunGitAsync(repositoryPath, $"switch {defaultBranch}");

        // 5. Fast-forward pull default branch to match remote
        Console.WriteLine($"[{repositoryPath}] Pulling latest '{defaultBranch}' (ff-only)...");
        try
        {
            await RunGitAsync(repositoryPath, $"pull --ff-only {remote} {defaultBranch}");
        }
        catch (Exception ex)
        {
            // If we can't fast-forward, better to bail out than do weird merges automatically
            throw new InvalidOperationException(
                $"Cannot fast-forward '{defaultBranch}' in {repositoryPath}. Resolve manually and retry. Details: {ex.Message}", ex);
        }

        // 6. Create/switch to feature branch
        var featureExists = await BranchExistsAsync(repositoryPath, featureBranchName);
        if (featureExists)
        {
            Console.WriteLine($"[{repositoryPath}] Branch '{featureBranchName}' already exists. Switching to it.");
            await RunGitAsync(repositoryPath, $"switch {featureBranchName}");
        }
        else
        {
            Console.WriteLine($"[{repositoryPath}] Creating and switching to branch '{featureBranchName}'...");
            await RunGitAsync(repositoryPath, $"switch -c {featureBranchName}");
        }

        // 7. Push feature branch to remote (set upstream)
        Console.WriteLine($"[{repositoryPath}] Pushing '{featureBranchName}' to {remote} (with upstream)...");
        await RunGitAsync(repositoryPath, $"push -u {remote} {featureBranchName}");

        //// 8. Pop stash onto the new feature branch (if we created one)
        //if (createdStash)
        //{
        //    Console.WriteLine($"[{repositoryPath}] Applying stashed changes onto '{featureBranchName}'...");
        //    try
        //    {
        //        await RunGitAsync(repositoryPath, "stash pop");
        //    }
        //    catch (Exception ex)
        //    {
        //        // Conflicts or other issues will throw here – just log it
        //        Console.WriteLine($"[{repositoryPath}] WARNING: 'stash pop' failed: {ex.Message}");
        //    }
        //}
    }

    static async Task<bool> HasUncommittedChangesAsync(string repositoryPath)
    {
        var output = await RunGitAsync(repositoryPath, "status --porcelain");
        return !string.IsNullOrWhiteSpace(output);
    }

    static async Task<bool> BranchExistsAsync(string repositoryPath, string branchName)
    {
        try
        {
            // rev-parse --verify returns 0 if the ref exists
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
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
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
