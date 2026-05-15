# AGENTS Rules

## Commit Rules

1. After every code or documentation change, create a Git commit immediately.
2. Every commit message must describe exactly what was changed.
3. Do not group unrelated changes in the same commit.
4. If a change touches multiple files for one feature/fix, include all related files in one commit with a precise message.
5. Use clear commit messages, for example:
   - `Add scan command for remote branch updates`
   - `Fix sync-branches checkout flow and restore original branch`
   - `Update README with scan and sync-branches usage`

## Execution Scope Rules

1. All implementation work must be based strictly on tasks defined in the project spec.
2. Do not start new features, refactors, or fixes unless they are mapped to an explicit spec task.
3. If a request is outside the current spec tasks, first update/specify the task in the spec, then implement.

## Testing and Output Validation Rules

1. Every change must be tested before it is considered complete.
2. For every modified command/feature, validate that runtime output matches the expected behavior.
3. Do not finalize or commit changes until output verification confirms correctness.
