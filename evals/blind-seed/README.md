# blind-seed

A synthetic fixture for `scripts/blind-seed-batch.sh` (issue 789, blind tests).

- `repo/` is a tiny .NET repository: `Discount.Total` has no discount yet. The script copies it to a temporary git repository.
- `issue.txt` is the issue text: "a discount of 10% applies to orders of 100 or more".
- `implementer-pin.txt` goes to the implementer so it ships the off-by-one (`> 100`) with a test that matches. Without blind tests that bug ships green.
  The pin travels as the task's `implementer_note`, which the blind sibling never receives: it sees the issue text alone.
