using System.Diagnostics;
using System.Text;

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

        if (IsGlobalOption(command))
        {
            return await RunSyncAsync(args);
        }

        return command switch
        {
            "sync" => await RunSyncAsync(commandArgs),
            "scan" => await RunScanAsync(commandArgs),
            "analyze-branches" => await RunAnalyzeBranchesAsync(commandArgs),
            "sync-project" => await RunSyncProjectAsync(commandArgs),
            "sync-branches" => await RunSyncBranchesAsync(commandArgs),
            "setup-alias" => await RunSetupAliasAsync(commandArgs),
            _ => UnknownCommand(command)
        };
    }

    private static bool IsGlobalOption(string arg) =>
        arg.StartsWith("--", StringComparison.Ordinal);

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

    private async Task<int> RunAnalyzeBranchesAsync(string[] args)
    {
        var options = ParseAnalyzeOptions(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            await AnalyzeRepositoryAsync(options.RepositoryPath, options);
            return 0;
        }

        var repositories = FindGitRepositories(options.RootDirectory).ToList();
        Console.WriteLine($"Found {repositories.Count} repositories in {options.RootDirectory}");
        foreach (var repository in repositories)
        {
            Console.WriteLine();
            await AnalyzeRepositoryAsync(repository, options);
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

    private async Task<int> RunSetupAliasAsync(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine("setup-alias does not accept arguments.");
            return 1;
        }

        var defaultAlias = "syncmydata";
        var defaultRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Console.Write($"Alias name [{defaultAlias}]: ");
        var aliasNameInput = Console.ReadLine()?.Trim();
        var aliasName = string.IsNullOrWhiteSpace(aliasNameInput) ? defaultAlias : aliasNameInput;
        if (!IsValidAliasName(aliasName))
        {
            Console.Error.WriteLine("Invalid alias name. Use letters, numbers, dash, or underscore.");
            return 1;
        }

        Console.Write($"Root folder [{defaultRoot}]: ");
        var rootInput = Console.ReadLine()?.Trim();
        var rootFolder = string.IsNullOrWhiteSpace(rootInput) ? defaultRoot : rootInput;
        rootFolder = Path.GetFullPath(rootFolder);
        if (!Directory.Exists(rootFolder))
        {
            Console.Error.WriteLine($"Root path does not exist: {rootFolder}");
            return 1;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localBin = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(localBin);

        var scriptPath = Path.Combine(localBin, aliasName);
        var dllPath = GetDllPathForAlias();
        var script = BuildAliasScript(dllPath, rootFolder);

        try
        {
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await RunProcessAsync("chmod", $"+x {scriptPath}");
            EnsurePathEntryInZshRc(home);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Failed to create alias: {ex.Message}");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"Alias command created: {scriptPath}");
        Console.WriteLine($"Configured root: {rootFolder}");
        Console.WriteLine();
        Console.WriteLine("Reload shell:");
        Console.WriteLine("  source ~/.zshrc");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine($"  {aliasName} scan");
        Console.WriteLine($"  {aliasName} sync --dry-run");
        Console.WriteLine($"  {aliasName} --dry-run");

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
                int progress;
                lock (gate)
                {
                    results.Add(result);
                    completed++;
                    progress = completed;
                }

                lock (consoleGate)
                {
                    Console.WriteLine($"[{progress}/{repositories.Count}] {repository}");
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
        var fetchResult = await RunGitAsync(repository, "fetch --all --prune", printOutput: false);
        if (fetchResult.ExitCode != 0)
        {
            var message = dryRun ? "DRY RUN: fetch failed" : "fetch failed";
            return new RepositoryScanResult(repository, false, new List<BranchState>(), message);
        }

        var branchState = await GetBranchStatesAsync(repository);
        var remoteChanged = branchState
            .Where(x => x.HasUpstream && (x.TrackShort.Contains('<') || x.TrackShort.Contains("<>")))
            .ToList();

        if (remoteChanged.Count == 0)
        {
            var message = dryRun ? "DRY RUN: no remote updates" : "no remote updates";
            return new RepositoryScanResult(repository, false, remoteChanged, message);
        }

        var foundMessage = dryRun ? "DRY RUN: remote updates found" : "remote updates found";
        return new RepositoryScanResult(repository, true, remoteChanged, foundMessage);
    }

    private async Task AnalyzeRepositoryAsync(string repositoryPath, AnalyzeOptions options)
    {
        Console.WriteLine($"Repository: {repositoryPath}");
        if (!options.DryRun)
        {
            await RunGitAsync(repositoryPath, "fetch --all --prune", printOutput: false);
        }

        var localRaw = await RunGitAsync(repositoryPath, "for-each-ref --format=%(refname:short)|%(upstream:short)|%(upstream:trackshort)|%(committerdate:iso8601) refs/heads", printOutput: false);
        var remoteRaw = await RunGitAsync(repositoryPath, "for-each-ref --format=%(refname:short)|%(committerdate:iso8601) refs/remotes/origin", printOutput: false);

        var localBranches = ParseLocalBranchRefs(localRaw.Output);
        var remoteBranches = ParseRemoteBranchRefs(remoteRaw.Output);
        var localNames = localBranches.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

        var rows = new List<BranchAnalysisRow>();
        foreach (var branch in localBranches)
        {
            var states = new List<string>();
            var ageState = GetAgeState(branch.LastCommitDate);
            states.Add(ageState);
            if (IsProtectedBranch(branch.Name))
            {
                states.Add("PROTECTED");
            }

            if (string.IsNullOrWhiteSpace(branch.Upstream))
            {
                states.Add("NO_UPSTREAM");
            }
            else if (branch.TrackShort.Contains("gone", StringComparison.OrdinalIgnoreCase))
            {
                states.Add("GONE_REMOTE");
            }

            if (branch.TrackShort.Contains("<>", StringComparison.Ordinal))
            {
                states.Add("DIVERGED");
            }

            var mergedState = await GetMergedStateAsync(repositoryPath, branch.Name);
            if (!string.IsNullOrWhiteSpace(mergedState))
            {
                states.Add(mergedState);
            }

            var lastAuthor = await GetLastAuthorAsync(repositoryPath, branch.Name);
            var ageDays = GetAgeInDays(branch.LastCommitDate);
            rows.Add(new BranchAnalysisRow(
                branch.Name,
                "LOCAL",
                string.Join(",", states.Distinct(StringComparer.Ordinal)),
                ToRelativeAge(branch.LastCommitDate),
                BuildRecommendation(states, ageDays),
                lastAuthor,
                ageDays,
                mergedState));
        }

        foreach (var remote in remoteBranches.Where(x => x.Name != "origin/HEAD" && x.Name != "origin"))
        {
            var shortRemote = remote.Name.StartsWith("origin/", StringComparison.Ordinal) ? remote.Name[7..] : remote.Name;
            if (!localNames.Contains(shortRemote))
            {
                var remoteAge = GetAgeInDays(remote.LastCommitDate);
                var remoteStates = new List<string> { "REMOTE_ONLY", GetAgeState(remote.LastCommitDate) };
                var remoteAuthor = await GetLastAuthorAsync(repositoryPath, remote.Name);
                rows.Add(new BranchAnalysisRow(
                    remote.Name,
                    "REMOTE_ONLY",
                    string.Join(",", remoteStates.Distinct(StringComparer.Ordinal)),
                    ToRelativeAge(remote.LastCommitDate),
                    remoteAge is >= 30 ? "Cleanup candidate (remote-only + 30d+)" : "Review and optionally checkout locally",
                    remoteAuthor,
                    remoteAge,
                    string.Empty));
            }
        }

        var filtered = ApplyAnalyzeFilters(rows, options);
        PrintBranchAnalysisTable(filtered.OrderBy(x => x.Branch, StringComparer.OrdinalIgnoreCase).ToList());
        PrintGroupedSummary(filtered);
        PrintCleanupCandidates(filtered);
    }

    private static List<LocalBranchRef> ParseLocalBranchRefs(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var parts = line.Split('|');
                return new LocalBranchRef(
                    parts.ElementAtOrDefault(0) ?? string.Empty,
                    parts.ElementAtOrDefault(1) ?? string.Empty,
                    parts.ElementAtOrDefault(2) ?? string.Empty,
                    ParseDate(parts.ElementAtOrDefault(3)));
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToList();
    }

    private static List<RemoteBranchRef> ParseRemoteBranchRefs(string output)
    {
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var parts = line.Split('|');
                return new RemoteBranchRef(parts.ElementAtOrDefault(0) ?? string.Empty, ParseDate(parts.ElementAtOrDefault(1)));
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToList();
    }

    private static DateTimeOffset ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var date) ? date : DateTimeOffset.MinValue;
    }

    private static string GetAgeState(DateTimeOffset lastCommitDate)
    {
        if (lastCommitDate == DateTimeOffset.MinValue)
        {
            return "OLD";
        }

        var days = (DateTimeOffset.UtcNow - lastCommitDate.ToUniversalTime()).TotalDays;
        if (days <= 14) return "ACTIVE";
        if (days <= 45) return "STALE";
        if (days <= 90) return "OLD";
        return "CLEANUP_CANDIDATE";
    }

    private static string ToRelativeAge(DateTimeOffset lastCommitDate)
    {
        if (lastCommitDate == DateTimeOffset.MinValue)
        {
            return "unknown";
        }

        var days = (int)Math.Max(0, (DateTimeOffset.UtcNow - lastCommitDate.ToUniversalTime()).TotalDays);
        return $"{days}d ago";
    }

    private static bool IsProtectedBranch(string branchName) =>
        branchName is "main" or "master" or "develop"
        || branchName.StartsWith("release/", StringComparison.Ordinal)
        || branchName.StartsWith("hotfix/", StringComparison.Ordinal);

    private static string BuildRecommendation(List<string> states, int? ageDays)
    {
        if (states.Contains("PROTECTED", StringComparer.Ordinal)) return "Keep (protected)";
        if (states.Contains("DIVERGED", StringComparer.Ordinal)) return "Manual review required";
        if ((states.Contains("MERGED_IN_DEVELOP", StringComparer.Ordinal) || states.Contains("MERGED_IN_MAIN", StringComparer.Ordinal)) && ageDays is >= 30)
        {
            return "Possible remove (merged + 30d+ old)";
        }
        if (states.Contains("MERGED_IN_DEVELOP", StringComparer.Ordinal) || states.Contains("MERGED_IN_MAIN", StringComparer.Ordinal)) return "Keep/Review (recently merged)";
        if (states.Contains("GONE_REMOTE", StringComparer.Ordinal)) return "Delete local";
        if (states.Contains("NO_UPSTREAM", StringComparer.Ordinal)) return "Publish or delete";
        if (states.Contains("CLEANUP_CANDIDATE", StringComparer.Ordinal)) return "Cleanup candidate";
        if (states.Contains("STALE", StringComparer.Ordinal) || states.Contains("OLD", StringComparer.Ordinal)) return "Review";
        return "Keep";
    }

    private static int? GetAgeInDays(DateTimeOffset lastCommitDate)
    {
        if (lastCommitDate == DateTimeOffset.MinValue)
        {
            return null;
        }

        return (int)Math.Max(0, (DateTimeOffset.UtcNow - lastCommitDate.ToUniversalTime()).TotalDays);
    }

    private async Task<string> GetMergedStateAsync(string repositoryPath, string branchName)
    {
        if (branchName is "develop" or "main" or "master")
        {
            return string.Empty;
        }

        var mergeInDevelop = await RunGitAsync(repositoryPath, $"merge-base --is-ancestor {EscapeGitArg(branchName)} develop", printOutput: false);
        if (mergeInDevelop.ExitCode == 0)
        {
            return "MERGED_IN_DEVELOP";
        }

        var mergeInMain = await RunGitAsync(repositoryPath, $"merge-base --is-ancestor {EscapeGitArg(branchName)} main", printOutput: false);
        if (mergeInMain.ExitCode == 0)
        {
            return "MERGED_IN_MAIN";
        }

        return string.Empty;
    }

    private async Task<string> GetLastAuthorAsync(string repositoryPath, string branchName)
    {
        var result = await RunGitAsync(repositoryPath, $"log -1 --format=\"%an <%ae>\" {EscapeGitArg(branchName)}", printOutput: false);
        return result.ExitCode == 0 ? result.Output.Trim() : "-";
    }

    private static List<BranchAnalysisRow> ApplyAnalyzeFilters(List<BranchAnalysisRow> rows, AnalyzeOptions options)
    {
        IEnumerable<BranchAnalysisRow> query = rows;

        if (!string.IsNullOrWhiteSpace(options.StateFilter))
        {
            query = query.Where(x => x.State.Contains(options.StateFilter!, StringComparison.OrdinalIgnoreCase));
        }

        if (options.MergedOnly)
        {
            query = query.Where(x => x.State.Contains("MERGED_IN_DEVELOP", StringComparison.Ordinal) || x.State.Contains("MERGED_IN_MAIN", StringComparison.Ordinal));
        }

        if (options.CleanupCandidatesOnly)
        {
            query = query.Where(x => x.Recommendation.Contains("Cleanup candidate", StringComparison.OrdinalIgnoreCase)
                || x.Recommendation.Contains("Possible remove", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(options.AuthorFilter))
        {
            query = query.Where(x => x.LastAuthor.Contains(options.AuthorFilter!, StringComparison.OrdinalIgnoreCase));
        }

        if (options.OlderThanDays is int olderThan)
        {
            query = query.Where(x => x.AgeDays is int age && age > olderThan);
        }

        return query.ToList();
    }

    private static void PrintBranchAnalysisTable(List<BranchAnalysisRow> rows)
    {
        Console.WriteLine($"Branches: {rows.Count}");

        var headers = new[] { "Branch", "Type", "Activity", "Merge State", "Score", "Last Author", "Recommendation" };
        var dataRows = rows.Select(row => new[]
        {
            row.Branch,
            row.Type,
            row.LastCommit,
            string.IsNullOrWhiteSpace(row.MergeState) ? "UNKNOWN" : row.MergeState,
            ComputeBranchScore(row).ToString(),
            row.LastAuthor,
            row.Recommendation
        }).ToList();

        var maxWidths = new[] { 42, 12, 10, 16, 5, 30, 36 };
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            var contentMax = dataRows.Count == 0 ? 0 : dataRows.Max(r => r[i].Length);
            widths[i] = Math.Min(maxWidths[i], Math.Max(headers[i].Length, contentMax));
        }

        PrintFixedTableLine(widths, '+', '-');
        PrintFixedTableRow(headers, widths);
        PrintFixedTableLine(widths, '+', '-');
        foreach (var row in dataRows)
        {
            PrintFixedTableRow(row, widths);
        }
        PrintFixedTableLine(widths, '+', '-');
    }

    private static void PrintFixedTableLine(int[] widths, char corner, char fill)
    {
        var parts = widths.Select(width => new string(fill, width + 2));
        Console.WriteLine($"{corner}{string.Join(corner, parts)}{corner}");
    }

    private static void PrintFixedTableRow(string[] cells, int[] widths)
    {
        var rendered = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var value = TruncateWithEllipsis(cells[i], widths[i]);
            rendered[i] = " " + value.PadRight(widths[i]) + " ";
        }

        Console.WriteLine($"|{string.Join("|", rendered)}|");
    }

    private static string TruncateWithEllipsis(string value, int maxWidth)
    {
        if (value.Length <= maxWidth)
        {
            return value;
        }

        if (maxWidth <= 1)
        {
            return value[..maxWidth];
        }

        return value[..(maxWidth - 1)] + "…";
    }

    private static int ComputeBranchScore(BranchAnalysisRow row)
    {
        if (row.State.Contains("PROTECTED", StringComparison.Ordinal)) return 100;
        if (row.State.Contains("DIVERGED", StringComparison.Ordinal)) return 40;
        if (row.State.Contains("CLEANUP_CANDIDATE", StringComparison.Ordinal)) return 10;
        if (row.State.Contains("OLD", StringComparison.Ordinal)) return 33;
        if (row.State.Contains("STALE", StringComparison.Ordinal)) return 55;
        if (row.State.Contains("ACTIVE", StringComparison.Ordinal) && (row.State.Contains("MERGED_IN_DEVELOP", StringComparison.Ordinal) || row.State.Contains("MERGED_IN_MAIN", StringComparison.Ordinal))) return 70;
        if (row.State.Contains("ACTIVE", StringComparison.Ordinal)) return 92;
        return 50;
    }

    private static void PrintGroupedSummary(List<BranchAnalysisRow> rows)
    {
        var remoteOnly = rows.Count(x => x.State.Contains("REMOTE_ONLY", StringComparison.Ordinal));
        var localMerged = rows.Count(x => x.State.Contains("MERGED_IN_DEVELOP", StringComparison.Ordinal) || x.State.Contains("MERGED_IN_MAIN", StringComparison.Ordinal));
        var stale = rows.Count(x => x.State.Contains("STALE", StringComparison.Ordinal) || x.State.Contains("OLD", StringComparison.Ordinal) || x.State.Contains("CLEANUP_CANDIDATE", StringComparison.Ordinal));
        var active = rows.Count(x => x.State.Contains("ACTIVE", StringComparison.Ordinal));
        var protectedCount = rows.Count(x => x.State.Contains("PROTECTED", StringComparison.Ordinal));

        Console.WriteLine();
        Console.WriteLine("Groups:");
        Console.WriteLine($"  REMOTE_ONLY: {remoteOnly}");
        Console.WriteLine($"  LOCAL_MERGED: {localMerged}");
        Console.WriteLine($"  STALE: {stale}");
        Console.WriteLine($"  ACTIVE: {active}");
        Console.WriteLine($"  PROTECTED: {protectedCount}");
    }

    private static void PrintCleanupCandidates(List<BranchAnalysisRow> rows)
    {
        var candidates = rows
            .Where(x => x.Recommendation.Contains("Possible remove", StringComparison.OrdinalIgnoreCase)
                || x.Recommendation.Contains("Cleanup candidate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"Cleanup Candidates: {candidates.Count}");
        if (candidates.Count == 0)
        {
            return;
        }

        var headers = new[] { "Branch", "Last Active", "Merged", "Safe To Delete" };
        var dataRows = candidates.Select(candidate =>
        {
            var merged = candidate.State.Contains("MERGED_IN_DEVELOP", StringComparison.Ordinal) || candidate.State.Contains("MERGED_IN_MAIN", StringComparison.Ordinal);
            var safeToDelete = merged && !candidate.State.Contains("PROTECTED", StringComparison.Ordinal) ? "Yes" : "Review Required";
            return new[]
            {
                candidate.Branch,
                candidate.LastCommit,
                merged ? "Yes" : "No",
                safeToDelete
            };
        }).ToList();

        var maxWidths = new[] { 42, 12, 8, 16 };
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            var contentMax = dataRows.Count == 0 ? 0 : dataRows.Max(r => r[i].Length);
            widths[i] = Math.Min(maxWidths[i], Math.Max(headers[i].Length, contentMax));
        }

        PrintFixedTableLine(widths, '+', '-');
        PrintFixedTableRow(headers, widths);
        PrintFixedTableLine(widths, '+', '-');
        foreach (var row in dataRows)
        {
            PrintFixedTableRow(row, widths);
        }
        PrintFixedTableLine(widths, '+', '-');
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
        Console.WriteLine("  analyze-branches (--repo <path> | --root <path>) [--dry-run] [--state <value>] [--merged] [--cleanup-candidates] [--author <name>] [--older-than <days>]");
        Console.WriteLine("  sync-project --repo <path> [--dry-run]");
        Console.WriteLine("  sync-branches --repo <path> [--dry-run]");
        Console.WriteLine("  setup-alias");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  sync           fetch + pull current branch in all repositories");
        Console.WriteLine("  scan           find repositories that have remote updates on tracked branches");
        Console.WriteLine("  analyze-branches analyze branch health and cleanup candidates");
        Console.WriteLine("  sync-project   fetch + pull current branch in one repository");
        Console.WriteLine("  sync-branches  fetch + pull all local branches with upstream in one repository");
        Console.WriteLine("  setup-alias    interactive setup for a global command with preconfigured root");
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
                case "--root":
                    // Allowed for compatibility with global alias wrappers that inject --root.
                    if (i + 1 >= args.Length)
                    {
                        return SyncBranchesOptions.Invalid("Missing value for --root");
                    }
                    i++;
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

    private static AnalyzeOptions ParseAnalyzeOptions(string[] args)
    {
        string? repositoryPath = null;
        var rootDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dryRun = false;
        string? stateFilter = null;
        var mergedOnly = false;
        var cleanupCandidatesOnly = false;
        string? authorFilter = null;
        int? olderThanDays = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--repo":
                    if (i + 1 >= args.Length) return AnalyzeOptions.Invalid("Missing value for --repo");
                    repositoryPath = Path.GetFullPath(args[++i]);
                    break;
                case "--root":
                    if (i + 1 >= args.Length) return AnalyzeOptions.Invalid("Missing value for --root");
                    rootDirectory = Path.GetFullPath(args[++i]);
                    break;
                case "--state":
                    if (i + 1 >= args.Length) return AnalyzeOptions.Invalid("Missing value for --state");
                    stateFilter = args[++i];
                    break;
                case "--merged":
                    mergedOnly = true;
                    break;
                case "--cleanup-candidates":
                    cleanupCandidatesOnly = true;
                    break;
                case "--author":
                    if (i + 1 >= args.Length) return AnalyzeOptions.Invalid("Missing value for --author");
                    authorFilter = args[++i];
                    break;
                case "--older-than":
                    if (i + 1 >= args.Length) return AnalyzeOptions.Invalid("Missing value for --older-than");
                    if (!int.TryParse(args[++i], out var parsedDays) || parsedDays < 0)
                    {
                        return AnalyzeOptions.Invalid("Invalid value for --older-than. Use a non-negative integer.");
                    }
                    olderThanDays = parsedDays;
                    break;
                default:
                    return AnalyzeOptions.Invalid($"Unknown option: {args[i]}");
            }
        }

        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            if (!Directory.Exists(repositoryPath)) return AnalyzeOptions.Invalid($"Repository path does not exist: {repositoryPath}");
            if (!Directory.Exists(Path.Combine(repositoryPath, ".git"))) return AnalyzeOptions.Invalid($"Path is not a Git repository: {repositoryPath}");
            return AnalyzeOptions.Valid(rootDirectory, repositoryPath, dryRun, stateFilter, mergedOnly, cleanupCandidatesOnly, authorFilter, olderThanDays);
        }

        if (!Directory.Exists(rootDirectory))
        {
            return AnalyzeOptions.Invalid($"Root path does not exist: {rootDirectory}");
        }

        return AnalyzeOptions.Valid(rootDirectory, null, dryRun, stateFilter, mergedOnly, cleanupCandidatesOnly, authorFilter, olderThanDays);
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

    private static bool IsValidAliasName(string aliasName) =>
        aliasName.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private static string GetDllPathForAlias()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDirectory, "SyncMyData.Cli.dll"));
    }

    private static string BuildAliasScript(string dllPath, string rootFolder)
    {
        return string.Join(
            '\n',
            "#!/bin/zsh",
            "set -e",
            $"dotnet {EscapeShellArg(dllPath)} \"$@\" --root {EscapeShellArg(rootFolder)}",
            string.Empty);
    }

    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {message}".Trim());
        }
    }

    private static void EnsurePathEntryInZshRc(string home)
    {
        var zshRcPath = Path.Combine(home, ".zshrc");
        var markerStart = "# >>> SyncMyData alias path >>>";
        var markerEnd = "# <<< SyncMyData alias path <<<";
        var block = string.Join(
            '\n',
            markerStart,
            "export PATH=\"$HOME/.local/bin:$PATH\"",
            markerEnd,
            string.Empty);

        if (!File.Exists(zshRcPath))
        {
            File.WriteAllText(zshRcPath, block);
            return;
        }

        var current = File.ReadAllText(zshRcPath);
        if (current.Contains(markerStart, StringComparison.Ordinal))
        {
            return;
        }

        if (!current.EndsWith('\n'))
        {
            current += '\n';
        }

        current += block;
        File.WriteAllText(zshRcPath, current);
    }

    private static string EscapeShellArg(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

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

    private readonly record struct LocalBranchRef(string Name, string Upstream, string TrackShort, DateTimeOffset LastCommitDate);
    private readonly record struct RemoteBranchRef(string Name, DateTimeOffset LastCommitDate);
    private readonly record struct BranchAnalysisRow(string Branch, string Type, string State, string LastCommit, string Recommendation, string LastAuthor, int? AgeDays, string MergeState);

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

    private readonly record struct AnalyzeOptions(
        string RootDirectory,
        string? RepositoryPath,
        bool DryRun,
        string? StateFilter,
        bool MergedOnly,
        bool CleanupCandidatesOnly,
        string? AuthorFilter,
        int? OlderThanDays,
        bool IsValid,
        string Error)
    {
        public static AnalyzeOptions Valid(
            string rootDirectory,
            string? repositoryPath,
            bool dryRun,
            string? stateFilter,
            bool mergedOnly,
            bool cleanupCandidatesOnly,
            string? authorFilter,
            int? olderThanDays) =>
            new(rootDirectory, repositoryPath, dryRun, stateFilter, mergedOnly, cleanupCandidatesOnly, authorFilter, olderThanDays, true, string.Empty);

        public static AnalyzeOptions Invalid(string error) =>
            new(string.Empty, null, false, null, false, false, null, null, false, error);
    }
}
