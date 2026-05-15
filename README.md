# SyncMyData

A C# CLI for automating local workflows.

Current features:
- scan all repositories and detect remote updates
- sync current branch in all repositories
- sync current branch in a single repository
- sync all local branches with upstream in one repository

## How It Works

### `scan`

1. Recursively scans a root folder.
2. Finds all directories that contain `.git`.
3. Runs `git fetch --all --prune` for each repository.
4. Checks all local branches that track an upstream branch.
5. Shows repositories where branches are behind/diverged from remote.

### `sync`

1. Recursively scans a root folder.
2. Finds all directories that contain `.git`.
3. Scans remote updates for each repository (`git fetch --all --prune` + tracking state check).
4. Displays exactly which repositories and branches are behind/diverged from remote.
5. For each changed repository, syncs all local branches with upstream:
   - `git checkout <branch>`
   - `git pull --ff-only`
6. Restores the original branch after repository sync.

### `sync-branches`

1. Targets one repository (`--repo <path>`).
2. Validates that the repository has no local changes.
3. Iterates all local branches that have upstream tracking.
4. For each branch:
   - `git checkout <branch>`
   - `git pull --ff-only`
5. Returns to the original branch.

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

### Setup a global alias with preconfigured root

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- setup-alias
```

The setup is interactive and asks:
- alias name (example: `syncmydata`)
- root folder (example: `/Users/songurov/Documents`)

It creates a global command in `~/.local/bin/<alias>` and updates `~/.zshrc` to include `~/.local/bin` in `PATH`.

After setup:

```bash
source ~/.zshrc
syncmydata scan
syncmydata sync --dry-run
syncmydata --dry-run
```

### Scan repositories for remote updates

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- scan --root /Users/<username>/Code
```

### Analyze branch health and cleanup candidates

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --root /Users/<username>/Code
```

Useful filters:

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo --state STALE
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo --merged
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo --cleanup-candidates
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo --author fiodor
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo --match songurov
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo --older-than 60
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- analyze-branches --repo /Users/<username>/Code/my-repo --copy
```

The command prints:
- Branch Intelligence Table
- grouped branch summary
- cleanup candidates table

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

Note: `sync` runs in two steps:
- Step 1: scan and show remote updates
- Step 2: sync all branches for changed repositories

`--dry-run` behavior:
- performs real remote scan (fetch + tracking analysis)
- does not run branch checkout/pull operations

### Sync only one project

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- sync-project --repo /Users/<username>/Code/my-repo
```

### Sync all branches in one repository

```bash
dotnet run --project SyncMyData.Cli/SyncMyData.Cli.csproj -- sync-branches --repo /Users/<username>/Code/my-repo
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

## Command Options

- `--root <path>`: root folder where scanning starts
- `--dry-run`: shows what would run without executing commands
- `--repo <path>`: target repository for `sync-project` or `sync-branches`

## Current Limitations

- Operations run sequentially (not in parallel).
- There is no config file yet for multiple root paths.
