# Tasks: Smart Branch Analyzer

## Phase 1: Foundations
- [ ] Add command parser entries:
  - `analyze-branches`
  - `cleanup-branches`
  - `branch-report`
- [ ] Add shared branch model:
  - branch name
  - branch type (local/remote)
  - upstream
  - ahead/behind/diverged flags
  - merged flags (develop/main)
  - last commit date
  - last author
  - protected flag
  - recommendation
- [ ] Add protected branch matcher (`main`, `master`, `develop`, `release/*`, `hotfix/*`).

## Phase 2: Analyze Branches
- [ ] Implement `analyze-branches --repo <path>`.
- [ ] Implement `analyze-branches --root <path>` aggregation.
- [ ] Implement state classification:
  - `ACTIVE`, `STALE`, `OLD`, `CLEANUP_CANDIDATE`
  - `MERGED_IN_DEVELOP`, `MERGED_IN_MAIN`
  - `NO_UPSTREAM`, `GONE_REMOTE`, `REMOTE_ONLY`, `DIVERGED`, `PROTECTED`
- [ ] Add deterministic table output per repository.

## Phase 3: Cleanup Branches
- [ ] Implement `cleanup-branches --repo <path> --dry-run`:
  - no side effects
  - clear planned delete actions
- [ ] Implement `cleanup-branches --repo <path>`:
  - enforce protected branch exclusion
  - enforce safe candidates only
  - print execution summary

## Phase 4: Report Export
- [ ] Implement `branch-report --root <path>`.
- [ ] Implement `branch-report --format markdown`.
- [ ] Ensure output is easy to redirect:
  - `... --format markdown > branches-report.md`

## Phase 5: Validation
- [ ] Add test repository fixtures or integration checks for:
  - merged branches
  - gone remote
  - no upstream
  - remote-only
  - diverged
  - protected branches
- [ ] Update README with usage examples and safety workflow.

## Execution Rule
All implementation must follow this task list and feature spec `specs/001-smart-branch-analyzer/spec.md`.
