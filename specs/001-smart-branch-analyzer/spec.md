# Feature Spec: Smart Branch Analyzer

## Feature ID
`001-smart-branch-analyzer`

## Goal
Add branch analysis and safe cleanup workflows across one repository or multiple repositories.

## Commands
- `analyze-branches --repo <path>`
- `analyze-branches --root <path>`
- `analyze-branches --dry-run`
- `cleanup-branches --repo <path> --dry-run`
- `cleanup-branches --repo <path>`
- `branch-report --root <path>`
- `branch-report --format markdown`

## Required Detection States
- `ACTIVE` (0-14 days)
- `STALE` (15-45 days)
- `OLD` (46-90 days)
- `CLEANUP_CANDIDATE` (90+ days)
- `MERGED_IN_DEVELOP`
- `MERGED_IN_MAIN`
- `NO_UPSTREAM`
- `GONE_REMOTE`
- `REMOTE_ONLY`
- `DIVERGED`
- `PROTECTED`

## Protected Branch Rules
Protected branches must never be deleted by cleanup:
- `main`
- `master`
- `develop`
- `release/*`
- `hotfix/*`

## Required Git Checks
1. Merged in develop/main
- `git branch --merged develop`
- `git branch -r --merged origin/develop`
- same checks for `main`/`origin/main` where available

2. Last activity
- `git for-each-ref --format="%(refname:short)|%(committerdate:iso8601)" refs/heads refs/remotes`

3. Upstream status
- local branch with no upstream => `NO_UPSTREAM`
- local branch with gone upstream => `GONE_REMOTE`
- remote branch with no local pair => `REMOTE_ONLY`

4. Divergence
- identify branches where local and remote both have unique commits => `DIVERGED`

5. Last author
- `git log -1 --format="%an <%ae>" <branch>`

## Output Format
For each repository:
- repository path/name
- table:
  - branch name
  - state(s)
  - last commit age/date
  - last author
  - recommendation

Example recommendation mapping:
- `MERGED_IN_DEVELOP` -> `Delete local + remote`
- `GONE_REMOTE` -> `Delete local`
- `NO_UPSTREAM` -> `Publish or delete`
- `DIVERGED` -> `Manual review required`

## Safety Rules
1. No destructive cleanup without explicit command.
2. Cleanup command must support `--dry-run` and default behavior in docs/examples must use `--dry-run` first.
3. Protected branches are excluded from cleanup always.

## Non-Goals (MVP Phase)
- Automatic delete of remote branches in bulk without explicit confirmation switch.
- Cross-repo parallel delete operations.

## Acceptance Criteria
1. `analyze-branches` reports all required states for `--repo`.
2. `analyze-branches --root` aggregates analysis across repositories.
3. `cleanup-branches --dry-run` shows exact delete plan, no side effects.
4. `cleanup-branches` executes only safe non-protected candidates.
5. `branch-report --format markdown` exports a readable report.
