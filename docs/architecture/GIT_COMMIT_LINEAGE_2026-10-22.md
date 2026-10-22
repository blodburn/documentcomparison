# DOCUMENTCOMPARE Git commit/tree lineage (read-only)

## User workflow

In Version History, use **Git 커밋 비교**, select a Git repository folder
(or one of its subfolders), then specify two revisions. Defaults are HEAD~1
and HEAD. Both revisions are resolved to actual immutable commit object IDs.
The before commit must be an ancestor of the after commit.
No checkout, reset, index mutation, working-tree copy or fetch occurs.
Git must be installed and accessible in the system PATH.

The result appears in the existing paginated folder-style change review,
with stable UnifiedChangeEvent IDs from before/after commits, paths and blobs.
The compared commits and repository root are displayed.

## Source of truth

GitCommitLineageComparer uses ProcessStartInfo.ArgumentList (no shell)
and bounded, timeout-protected raw subprocess output.

- Git rev-parse resolves the repository root and verifies commit references.
- Git merge-base --is-ancestor requires an ordered history.
- Git ls-tree -r -z --full-tree enumerates committed blob identities;
  uncommitted or untracked files cannot influence the comparison.
- A same-path changed blob means a modified file.
  Deleted/added paths with identical blob IDs represent a proven exact
  file move ONLY when the ID is unique in both entire commit trees.
  Other cases remain ordinary additions and deletions.
- Git diff-tree --no-ext-diff --no-textconv --name-status -z -M75% supplies
  heuristic rename evidence. Changed-byte matches with score >=75% are
  explicitly called **이름 변경 후보(Git)** rather than proven identity.
  It never overrides a stronger exact blob match. Similarity is not a
  calibrated probability.
- NUL-delimited paths support Unicode and unescaped whitespace.
  Symlinks and submodule commits are skipped; regular Git blobs are included.
- Git lineage is additive to, and separate from, the existing Rust line,
  folder and AST analyses. Git-aware AST tracking of declarations across
  arbitrary historical snapshots is not yet implemented.

## Limits and safeguards

- Read-only operations only: no checkout, fetch, reset, or repository writes.
- Up to 20,000 regular files per commit, 16 MB command output, 25 seconds
  per Git command.
- ArgumentList and commit resolution reject option injection. Invalid
  revision strings, non-ancestral pairs, process errors or truncated output
  are not silently treated as successful comparisons.
- Git rename tracking compares two selected trees, not every intermediate
  rename in a multi-commit history.
- No app version bump or GitHub Release is part of the source change.
