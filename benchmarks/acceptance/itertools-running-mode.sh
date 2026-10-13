set -e
cat > tests/test_chargehand_bench_running_mode.py <<'EOF'
import random
from collections import Counter
from collections.abc import Iterator
from unittest import TestCase

import more_itertools as mi
from more_itertools import recipes


def naive(items, maxlen=None):
    """The most common value of each prefix or window; a tie goes to the value that comes first in the window."""
    out = []
    for i in range(len(items)):
        window = items[max(0, i + 1 - maxlen):i + 1] if maxlen else items[:i + 1]
        counts = Counter(window)
        best = max(counts.values())
        out.append(next(v for v in window if counts[v] == best))
    return out


class RunningModeAcceptance(TestCase):
    def test_examples(self):
        self.assertEqual(list(mi.running_mode([1, 2, 2, 1, 1])), [1, 1, 2, 1, 1])
        self.assertEqual(list(mi.running_mode('abbcc')), ['a', 'a', 'b', 'b', 'b'])
        self.assertEqual(list(mi.running_mode([1, 2, 2, 3, 3, 3], maxlen=2)), [1, 1, 2, 2, 3, 3])
        self.assertEqual(list(mi.running_mode([])), [])

    def test_matches_the_definition_without_a_window(self):
        rng = random.Random(7)
        for _ in range(150):
            items = [rng.randrange(5) for _ in range(rng.randrange(0, 40))]
            self.assertEqual(list(mi.running_mode(items)), naive(items), items)

    def test_matches_the_definition_with_a_window(self):
        rng = random.Random(11)
        for _ in range(150):
            items = [rng.choice('abcd') for _ in range(rng.randrange(0, 40))]
            maxlen = rng.choice([1, 2, 3, 5, 10])
            self.assertEqual(list(mi.running_mode(items, maxlen=maxlen)), naive(items, maxlen), (items, maxlen))

    def test_is_lazy_and_takes_any_iterable(self):
        result = mi.running_mode(iter([3, 3, 4]))
        self.assertIsInstance(result, Iterator)
        self.assertEqual(list(result), [3, 3, 3])
        pulled = []

        def source():
            for x in [1, 1, 2]:
                pulled.append(x)
                yield x

        first = next(mi.running_mode(source()))
        self.assertEqual((first, pulled), (1, [1]))

    def test_window_size_is_checked_like_running_median(self):
        for bad in (0, -1):
            with self.assertRaises(ValueError):
                list(mi.running_mode([1, 2], maxlen=bad))

    def test_is_exported_typed_and_documented(self):
        self.assertIn('running_mode', recipes.__all__)
        self.assertIs(mi.running_mode, recipes.running_mode)
        with open('more_itertools/recipes.pyi', encoding='utf-8') as f:
            self.assertIn('def running_mode(', f.read())
        with open('docs/api.rst', encoding='utf-8') as f:
            self.assertIn('running_mode', f.read())
EOF
python3 -m unittest -q tests.test_chargehand_bench_running_mode
