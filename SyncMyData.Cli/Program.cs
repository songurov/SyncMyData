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

        if (!string.Equals(args[0], "sync", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            PrintHelp();
            return 1;
        }

        var options = ParseSyncOptions(args.Skip(1).ToArray());
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

            var fetchExit = await RunGitAsync(repository, "fetch --all --prune");
            if (fetchExit != 0)
            {
                failures.Add($"{repository} (fetch failed)");
                continue;
            }

            var pullExit = await RunGitAsync(repository, "pull --ff-only");
            if (pullExit != 0)
            {
                failures.Add($"{repository} (pull failed)");
            }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("Sync completed successfully.");
            return 0;
        }

        Console.WriteLine("Sync completed with failures:");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return 2;
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "help";

    private static void PrintHelp()
    {
        Console.WriteLine("SyncMyData CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  sync [--root <path>] [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  sync");
        Console.WriteLine("  sync --root /Users/you/Code");
        Console.WriteLine("  sync --dry-run");
    }

    private static SyncOptions ParseSyncOptions(string[] args)
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
                        return SyncOptions.Invalid("Missing value for --root");
                    }
                    rootDirectory = args[++i];
                    break;
                default:
                    return SyncOptions.Invalid($"Unknown option: {args[i]}");
            }
        }

        if (!Directory.Exists(rootDirectory))
        {
            return SyncOptions.Invalid($"Root path does not exist: {rootDirectory}");
        }

        return SyncOptions.Valid(Path.GetFullPath(rootDirectory), dryRun);
    }

    private static IEnumerable<string> FindGitRepositories(string rootDirectory)
    {
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

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Push(child);
            }
        }
    }

    private static async Task<int> RunGitAsync(string repositoryPath, string arguments)
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

        var outputTask = RelayAsync(process.StandardOutput);
        var errorTask = RelayAsync(process.StandardError);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync());

        return process.ExitCode;
    }

    private static async Task RelayAsync(StreamReader streamReader)
    {
        while (true)
        {
            var line = await streamReader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private readonly record struct SyncOptions(string RootDirectory, bool DryRun, bool IsValid, string Error)
    {
        public static SyncOptions Valid(string rootDirectory, bool dryRun) =>
            new(rootDirectory, dryRun, true, string.Empty);

        public static SyncOptions Invalid(string error) =>
            new(string.Empty, false, false, error);
    }
}
