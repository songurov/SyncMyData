using System.Diagnostics;

var cli = new RepoSyncCli();
return await cli.RunAsync(args);

internal sealed class RepoSyncCli
{
    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var commandArgs = args.Skip(1).ToArray();

        return command switch
        {
            "sync" => await RunSyncAsync(commandArgs),
            "scan" => await RunScanAsync(commandArgs),
            "sync-project" => await RunSyncProjectAsync(commandArgs),
            "sync-branches" => await RunSyncBranchesAsync(commandArgs),
            _ => UnknownCommand(command)
        };
    }

    private async Task<int> RunSyncAsync(string[] args)
    {
        var options = ParseRootedOptions(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            return 1;
        }

        var repositories = FindGitRepositories(options.RootDirectory).ToList();
        Console.WriteLine($"Found {repositories.Count} repositories in {options.RootDirectory}");
        if (repositories.Count == 0)
        {
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Step 1/2: scanning remote updates...");

        var scanResults = await ScanRepositoriesForRemoteUpdatesAsync(repositories, options.DryRun);
        var changedRepositories = scanResults.Where(x => x.HasRemoteUpdates).ToList();

        Console.WriteLine();
        Console.WriteLine($"Repositories with remote updates: {changedRepositories.Count}");
        foreach (var result in changedRepositories)
        {
            Console.WriteLine($"  - {result.RepositoryPath}");
            foreach (var branch in result.RemoteUpdatedBranches)
            {
                Console.WriteLine($"    {branch.Name} ({branch.TrackShort})");
            }
        }

        if (changedRepositories.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No remote updates found.");
            return 0;
        }

        var failures = new List<string>();
        Console.WriteLine();
        Console.WriteLine("Step 2/2: syncing all branches per changed repository...");

        foreach (var result in changedRepositories)
        {
            var repository = result.RepositoryPath;
            Console.WriteLine();
            Console.WriteLine($"[{repository}]");
            var syncResult = await SyncAllBranchesInRepositoryAsync(repository, options.DryRun);
            if (!syncResult.Success)
            {
                failures.Add($"{repository} ({syncResult.Error})");
            }
        }

        return PrintSummary(failures, "Full sync completed successfully.", "Full sync completed with failures:");
    }

    private async Task<int> RunScanAsync(string[] args)
    {
        var options = ParseRootedOptions(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            return 1;
        }

        var repositories = FindGitRepositories(options.RootDirectory).ToList();
        Console.WriteLine($"Found {repositories.Count} repositories in {options.RootDirectory}");
        if (repositories.Count == 0)
        {
            return 0;
        }

        var scanResults = await ScanRepositoriesForRemoteUpdatesAsync(repositories, options.DryRun);
        var changedRepositories = scanResults.Where(x => x.HasRemoteUpdates).ToList();

        Console.WriteLine();
        Console.WriteLine($"Repositories with remote updates: {changedRepositories.Count}");
        foreach (var result in changedRepositories)
        {
            Console.WriteLine($"  - {result.RepositoryPath}");
            foreach (var branch in result.RemoteUpdatedBranches)
            {
                Console.WriteLine($"    {branch.Name} ({branch.TrackShort})");
            }
        }

        return 0;
    }

    private async Task<int> RunSyncProjectAsync(string[] args)
    {
        var options = ParseSyncBranchesOptions(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            return 1;
        }

        Console.WriteLine($"Repository: {options.RepositoryPath}");

        if (options.DryRun)
        {
            Console.WriteLine("  DRY RUN: git fetch --all --prune");
            Console.WriteLine("  DRY RUN: git pull --ff-only");
            return 0;
        }

        var fetchResult = await RunGitAsync(options.RepositoryPath, "fetch --all --prune", printOutput: true);
        if (fetchResult.ExitCode != 0)
        {
            Console.Error.WriteLine("Project sync failed: fetch error.");
            return 2;
        }

        var pullResult = await RunGitAsync(options.RepositoryPath, "pull --ff-only", printOutput: true);
        if (pullResult.ExitCode != 0)
        {
            Console.Error.WriteLine("Project sync failed: pull error.");
            return 2;
        }

        Console.WriteLine("Project sync completed successfully.");
        return 0;
    }

    private async Task<int> RunSyncBranchesAsync(string[] args)
    {
        var options = ParseSyncBranchesOptions(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            return 1;
        }

        Console.WriteLine($"Repository: {options.RepositoryPath}");
        if (options.DryRun)
        {
            Console.WriteLine("DRY RUN enabled");
        }

        var statusResult = await RunGitAsync(options.RepositoryPath, "status --porcelain", printOutput: false);
        if (statusResult.ExitCode != 0)
        {
            Console.Error.WriteLine("Cannot read repository status.");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(statusResult.Output))
        {
            Console.Error.WriteLine("Repository has local changes. Commit or stash before syncing all branches.");
            return 2;
        }

        var originalBranchResult = await RunGitAsync(options.RepositoryPath, "rev-parse --abbrev-ref HEAD", printOutput: false);
        if (originalBranchResult.ExitCode != 0 || string.IsNullOrWhiteSpace(originalBranchResult.Output))
        {
            Console.Error.WriteLine("Cannot determine current branch.");
            return 2;
        }

        var originalBranch = originalBranchResult.Output.Trim();
        var branchStates = await GetBranchStatesAsync(options.RepositoryPath);
        var branches = branchStates.Where(x => x.HasUpstream).Select(x => x.Name).ToList();

        if (branches.Count == 0)
        {
            Console.WriteLine("No local branches with upstream were found.");
            return 0;
        }

        var failures = new List<string>();
        Console.WriteLine($"Branches with upstream: {branches.Count}");

        foreach (var branch in branches)
        {
            Console.WriteLine();
            Console.WriteLine($"[{branch}]");

            if (options.DryRun)
            {
                Console.WriteLine($"  DRY RUN: git checkout {branch}");
                Console.WriteLine("  DRY RUN: git pull --ff-only");
                continue;
            }

            var checkoutResult = await RunGitAsync(options.RepositoryPath, $"checkout {EscapeGitArg(branch)}", printOutput: true);
            if (checkoutResult.ExitCode != 0)
            {
                failures.Add($"{branch} (checkout failed)");
                continue;
            }

            var pullResult = await RunGitAsync(options.RepositoryPath, "pull --ff-only", printOutput: true);
            if (pullResult.ExitCode != 0)
            {
                failures.Add($"{branch} (pull failed)");
            }
        }

        if (!options.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine($"Restoring original branch: {originalBranch}");
            await RunGitAsync(options.RepositoryPath, $"checkout {EscapeGitArg(originalBranch)}", printOutput: true);
        }

        return PrintSummary(
            failures,
            "All branches synced successfully.",
            "Branch sync completed with failures:");
    }

    private static async Task<List<BranchState>> GetBranchStatesAsync(string repositoryPath)
    {
        const string command = "for-each-ref --format=%(refname:short)|%(upstream:short)|%(upstream:trackshort) refs/heads";
        var result = await RunGitAsync(repositoryPath, command, printOutput: false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return new List<BranchState>();
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var parts = line.Split('|');
                var name = parts.ElementAtOrDefault(0) ?? string.Empty;
                var upstream = parts.ElementAtOrDefault(1) ?? string.Empty;
                var trackShort = parts.ElementAtOrDefault(2) ?? string.Empty;
                return new BranchState(name, upstream, trackShort);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToList();
    }

    private async Task<List<RepositoryScanResult>> ScanRepositoriesForRemoteUpdatesAsync(List<string> repositories, bool dryRun)
    {
        var results = new List<RepositoryScanResult>();
        var gate = new object();
        var consoleGate = new object();
        var maxParallel = Math.Min(Environment.ProcessorCount, 8);
        var semaphore = new SemaphoreSlim(maxParallel);
        var completed = 0;

        var tasks = repositories.Select(async repository =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await AnalyzeRepositoryRemoteUpdatesAsync(repository, dryRun);
                lock (gate)
                {
                    results.Add(result);
                    completed++;
                }

                lock (consoleGate)
                {
                    Console.WriteLine($"[{completed}/{repositories.Count}] {repository}");
                    Console.WriteLine($"  {result.ScanMessage}");
                    if (result.HasRemoteUpdates)
                    {
                        foreach (var branch in result.RemoteUpdatedBranches)
                        {
                            Console.WriteLine($"  - {branch.Name} ({branch.TrackShort})");
                        }
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(x => x.RepositoryPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<RepositoryScanResult> AnalyzeRepositoryRemoteUpdatesAsync(string repository, bool dryRun)
    {
        if (dryRun)
        {
            return new RepositoryScanResult(
                repository,
                false,
                new List<BranchState>(),
                "DRY RUN: would fetch remote refs and inspect tracking state");
        }

        var fetchResult = await RunGitAsync(repository, "fetch --all --prune", printOutput: false);
        if (fetchResult.ExitCode != 0)
        {
            return new RepositoryScanResult(repository, false, new List<BranchState>(), "fetch failed");
        }

        var branchState = await GetBranchStatesAsync(repository);
        var remoteChanged = branchState
            .Where(x => x.HasUpstream && (x.TrackShort.Contains('<') || x.TrackShort.Contains("<>")))
            .ToList();

        if (remoteChanged.Count == 0)
        {
            return new RepositoryScanResult(repository, false, remoteChanged, "no remote updates");
        }

        return new RepositoryScanResult(repository, true, remoteChanged, "remote updates found");
    }

    private async Task<SyncRepositoryResult> SyncAllBranchesInRepositoryAsync(string repositoryPath, bool dryRun)
    {
        var statusResult = await RunGitAsync(repositoryPath, "status --porcelain", printOutput: false);
        if (statusResult.ExitCode != 0)
        {
            return SyncRepositoryResult.Fail("cannot read status");
        }

        if (!string.IsNullOrWhiteSpace(statusResult.Output))
        {
            return SyncRepositoryResult.Fail("has local changes, skipped");
        }

        var originalBranchResult = await RunGitAsync(repositoryPath, "rev-parse --abbrev-ref HEAD", printOutput: false);
        if (originalBranchResult.ExitCode != 0 || string.IsNullOrWhiteSpace(originalBranchResult.Output))
        {
            return SyncRepositoryResult.Fail("cannot determine current branch");
        }

        var originalBranch = originalBranchResult.Output.Trim();
        var branchStates = await GetBranchStatesAsync(repositoryPath);
        var branches = branchStates.Where(x => x.HasUpstream).Select(x => x.Name).ToList();
        if (branches.Count == 0)
        {
            Console.WriteLine("  no local branches with upstream");
            return SyncRepositoryResult.Ok();
        }

        Console.WriteLine($"  branches to sync: {branches.Count}");
        foreach (var branch in branches)
        {
            Console.WriteLine($"  [{branch}]");
            if (dryRun)
            {
                Console.WriteLine($"    DRY RUN: git checkout {branch}");
                Console.WriteLine("    DRY RUN: git pull --ff-only");
                continue;
            }

            var checkoutResult = await RunGitAsync(repositoryPath, $"checkout {EscapeGitArg(branch)}", printOutput: true);
            if (checkoutResult.ExitCode != 0)
            {
                return SyncRepositoryResult.Fail($"checkout failed on branch {branch}");
            }

            var pullResult = await RunGitAsync(repositoryPath, "pull --ff-only", printOutput: true);
            if (pullResult.ExitCode != 0)
            {
                return SyncRepositoryResult.Fail($"pull failed on branch {branch}");
            }
        }

        if (!dryRun)
        {
            Console.WriteLine($"  restoring branch: {originalBranch}");
            var restoreResult = await RunGitAsync(repositoryPath, $"checkout {EscapeGitArg(originalBranch)}", printOutput: true);
            if (restoreResult.ExitCode != 0)
            {
                return SyncRepositoryResult.Fail("failed to restore original branch");
            }
        }

        return SyncRepositoryResult.Ok();
    }

    private static async Task<CurrentBranchInfo> GetCurrentBranchInfoAsync(string repositoryPath)
    {
        var branchResult = await RunGitAsync(repositoryPath, "rev-parse --abbrev-ref HEAD", printOutput: false);
        if (branchResult.ExitCode != 0)
        {
            return CurrentBranchInfo.Invalid();
        }

        var branch = branchResult.Output.Trim();
        if (string.IsNullOrWhiteSpace(branch) || branch == "HEAD")
        {
            return CurrentBranchInfo.Invalid();
        }

        var upstreamResult = await RunGitAsync(repositoryPath, "rev-parse --abbrev-ref --symbolic-full-name @{u}", printOutput: false);
        var upstream = upstreamResult.ExitCode == 0
            ? upstreamResult.Output.Trim()
            : "no-upstream";

        return CurrentBranchInfo.Valid(branch, upstream);
    }

    private static int PrintSummary(List<string> failures, string successMessage, string failureHeader)
    {
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine(successMessage);
            return 0;
        }

        Console.WriteLine(failureHeader);
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return 2;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine("SyncMyData CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  sync [--root <path>] [--dry-run]  (scan remote updates, then sync all branches in changed repos)");
        Console.WriteLine("  scan [--root <path>] [--dry-run]");
        Console.WriteLine("  sync-project --repo <path> [--dry-run]");
        Console.WriteLine("  sync-branches --repo <path> [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  sync           fetch + pull current branch in all repositories");
        Console.WriteLine("  scan           find repositories that have remote updates on tracked branches");
        Console.WriteLine("  sync-project   fetch + pull current branch in one repository");
        Console.WriteLine("  sync-branches  fetch + pull all local branches with upstream in one repository");
    }

    private static RootedOptions ParseRootedOptions(string[] args)
    {
        var rootDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--root":
                    if (i + 1 >= args.Length)
                    {
                        return RootedOptions.Invalid("Missing value for --root");
                    }
                    rootDirectory = args[++i];
                    break;
                default:
                    return RootedOptions.Invalid($"Unknown option: {args[i]}");
            }
        }

        if (!Directory.Exists(rootDirectory))
        {
            return RootedOptions.Invalid($"Root path does not exist: {rootDirectory}");
        }

        return RootedOptions.Valid(Path.GetFullPath(rootDirectory), dryRun);
    }

    private static SyncBranchesOptions ParseSyncBranchesOptions(string[] args)
    {
        var repositoryPath = string.Empty;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--repo":
                    if (i + 1 >= args.Length)
                    {
                        return SyncBranchesOptions.Invalid("Missing value for --repo");
                    }
                    repositoryPath = args[++i];
                    break;
                default:
                    return SyncBranchesOptions.Invalid($"Unknown option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return SyncBranchesOptions.Invalid("Missing required option: --repo <path>");
        }

        repositoryPath = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(repositoryPath))
        {
            return SyncBranchesOptions.Invalid($"Repository path does not exist: {repositoryPath}");
        }

        if (!Directory.Exists(Path.Combine(repositoryPath, ".git")))
        {
            return SyncBranchesOptions.Invalid($"Path is not a Git repository: {repositoryPath}");
        }

        return SyncBranchesOptions.Valid(repositoryPath, dryRun);
    }

    private static IEnumerable<string> FindGitRepositories(string rootDirectory)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var pending = new Stack<string>();
        pending.Push(rootDirectory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var gitDirectory = Path.Combine(current, ".git");
            if (Directory.Exists(gitDirectory))
            {
                yield return current;
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(current, "*", enumerationOptions))
                {
                    pending.Push(child);
                }
            }
            catch
            {
                continue;
            }
        }
    }

    private static async Task<GitCommandResult> RunGitAsync(string repositoryPath, string arguments, bool printOutput)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (printOutput)
        {
            PrintLines(standardOutput);
            PrintLines(standardError);
        }

        return new GitCommandResult(process.ExitCode, standardOutput, standardError);
    }

    private static void PrintLines(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Console.WriteLine($"  {line}");
        }
    }

    private static string EscapeGitArg(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private readonly record struct RootedOptions(string RootDirectory, bool DryRun, bool IsValid, string Error)
    {
        public static RootedOptions Valid(string rootDirectory, bool dryRun) =>
            new(rootDirectory, dryRun, true, string.Empty);

        public static RootedOptions Invalid(string error) =>
            new(string.Empty, false, false, error);
    }

    private readonly record struct SyncBranchesOptions(string RepositoryPath, bool DryRun, bool IsValid, string Error)
    {
        public static SyncBranchesOptions Valid(string repositoryPath, bool dryRun) =>
            new(repositoryPath, dryRun, true, string.Empty);

        public static SyncBranchesOptions Invalid(string error) =>
            new(string.Empty, false, false, error);
    }

    private readonly record struct GitCommandResult(int ExitCode, string Output, string Error);

    private readonly record struct BranchState(string Name, string Upstream, string TrackShort)
    {
        public bool HasUpstream => !string.IsNullOrWhiteSpace(Upstream);
    }

    private readonly record struct CurrentBranchInfo(string Branch, string Upstream, bool IsValid)
    {
        public static CurrentBranchInfo Valid(string branch, string upstream) =>
            new(branch, string.IsNullOrWhiteSpace(upstream) ? "no-upstream" : upstream, true);

        public static CurrentBranchInfo Invalid() =>
            new(string.Empty, string.Empty, false);
    }

    private readonly record struct RepositoryScanResult(
        string RepositoryPath,
        bool HasRemoteUpdates,
        List<BranchState> RemoteUpdatedBranches,
        string ScanMessage);

    private readonly record struct SyncRepositoryResult(bool Success, string Error)
    {
        public static SyncRepositoryResult Ok() => new(true, string.Empty);
        public static SyncRepositoryResult Fail(string error) => new(false, error);
    }
}
