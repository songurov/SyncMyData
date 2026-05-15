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

        var failures = new List<string>();
        foreach (var repository in repositories)
        {
            Console.WriteLine();
            Console.WriteLine($"[{repository}]");

            if (options.DryRun)
            {
                Console.WriteLine("  DRY RUN: git fetch --all --prune");
                Console.WriteLine("  DRY RUN: git pull --ff-only");
                continue;
            }

            var fetchResult = await RunGitAsync(repository, "fetch --all --prune", printOutput: true);
            if (fetchResult.ExitCode != 0)
            {
                failures.Add($"{repository} (fetch failed)");
                continue;
            }

            var pullResult = await RunGitAsync(repository, "pull --ff-only", printOutput: true);
            if (pullResult.ExitCode != 0)
            {
                failures.Add($"{repository} (pull failed)");
            }
        }

        return PrintSummary(failures, "Sync completed successfully.", "Sync completed with failures:");
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

        var changedRepositories = new List<string>();
        foreach (var repository in repositories)
        {
            Console.WriteLine();
            Console.WriteLine($"[{repository}]");

            if (options.DryRun)
            {
                Console.WriteLine("  DRY RUN: git fetch --all --prune");
                Console.WriteLine("  DRY RUN: inspect tracking state for all local branches");
                continue;
            }

            var fetchResult = await RunGitAsync(repository, "fetch --all --prune", printOutput: false);
            if (fetchResult.ExitCode != 0)
            {
                Console.WriteLine("  fetch failed");
                continue;
            }

            var branchState = await GetBranchStatesAsync(repository);
            var remoteChanged = branchState
                .Where(x => x.HasUpstream && (x.TrackShort.Contains('<') || x.TrackShort.Contains("<>")))
                .ToList();

            if (remoteChanged.Count == 0)
            {
                Console.WriteLine("  no remote updates");
                continue;
            }

            changedRepositories.Add(repository);
            Console.WriteLine("  remote updates found:");
            foreach (var branch in remoteChanged)
            {
                Console.WriteLine($"  - {branch.Name} ({branch.TrackShort})");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Repositories with remote updates: {changedRepositories.Count}");
        foreach (var repository in changedRepositories)
        {
            Console.WriteLine($"  - {repository}");
        }

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
        Console.WriteLine("  sync [--root <path>] [--dry-run]");
        Console.WriteLine("  scan [--root <path>] [--dry-run]");
        Console.WriteLine("  sync-branches --repo <path> [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  sync           fetch + pull current branch in all repositories");
        Console.WriteLine("  scan           find repositories that have remote updates on tracked branches");
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
}
