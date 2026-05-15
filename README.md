# SyncMyData

A C# CLI for automating local workflows.

The first implemented feature is automatic synchronization of Git repositories on macOS.

## How It Works

The `sync` command:

1. Recursively scans a root folder.
2. Finds all directories that contain `.git`.
3. Runs the following commands for each repository:
   - `git fetch --all --prune`
   - `git pull --ff-only`
4. Prints status and any errors per repository.

## Requirements

- macOS
- .NET SDK 10+ (or a compatible SDK for this project)
- Git installed and configured

Quick check:

```bash
dotnet --version
git --version
```

## Installation

From the project root:

```bash
dotnet restore SyncMyData.Cli/SyncMyData.Cli.csproj
dotnet build SyncMyData.Cli/SyncMyData.Cli.csproj -c Release
```

## Usage

### Help

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- --help
```

### Dry Run (does not execute Git commands)

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- sync --dry-run
```

### Real Sync for a Specific Folder

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- sync --root /Users/<username>/Code
```

### Real Sync for Current User Home

If you do not pass `--root`, the tool automatically uses the current user's home directory.

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- sync
```

## Local Publish (Optional)

To generate a local executable:

```bash
dotnet publish SyncMyData.Cli/SyncMyData.Cli.csproj -c Release -o ./publish
```

Then run:

```bash
./publish/SyncMyData.Cli sync --dry-run
```

## `sync` Command Options

- `--root <path>`: root folder where scanning starts
- `--dry-run`: shows what would run without executing commands

## Current Limitations

- Sync runs sequentially (not in parallel).
- There is no config file yet for multiple root paths.
