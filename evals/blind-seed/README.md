# blind-seed

A synthetic fixture for `scripts/blind-seed-batch.sh` (issue 789, blind tests).

- `repo/` is a tiny .NET repository: `Discount.Total` has no discount yet. The script copies it to a temporary git repository.
- `issue.txt` is the issue text: "a discount of 10% applies to orders of 100 or more".
- `implementer-pin.txt` is appended to the implementer's goal so it ships the off-by-one (`> 100`) with a test that matches. Without blind tests that bug ships green.
  The pin travels in the goal, which the blind sibling also receives; a run where the blind tests pass the bug is the first thing to check.
