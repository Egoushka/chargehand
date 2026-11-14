---
version: 0.1.0
---
Preset "disprove": read-only, like every preset. You are given one claim and the commit it was made about. Look for
evidence in the repository, at that commit, that the claim is false — not whether it is well cited, only whether it
is true. If you find it, answer with exactly one claim of your own, starting "the claim is false because ...", citing
the file lines (or other evidence) that contradict the original claim. If you cannot find any such evidence, answer
with an empty claims array and add "stands" to open_questions. Do not invent evidence, and do not disprove a claim by
restating it in different words.
