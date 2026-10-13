set -e
cat > tests/test_chargehand_bench_argsort.py <<'EOF'
from unittest import TestCase

import more_itertools as mi
from more_itertools import more


class ArgsortAcceptance(TestCase):
    def test_indices_that_sort(self):
        self.assertEqual(mi.argsort([3, 1, 2]), [1, 2, 0])
        self.assertEqual(mi.argsort('hello'), [1, 0, 2, 3, 4])
        self.assertEqual(mi.argsort([]), [])

    def test_returns_a_list(self):
        self.assertIsInstance(mi.argsort([2, 1]), list)

    def test_equal_items_keep_their_order(self):
        self.assertEqual(mi.argsort([2, 1, 2, 1]), [1, 3, 0, 2])

    def test_reverse_keeps_the_order_of_equal_items(self):
        self.assertEqual(mi.argsort([2, 1, 2, 1], reverse=True), [0, 2, 1, 3])
        self.assertEqual(mi.argsort([1, 3, 2], reverse=True), [1, 2, 0])

    def test_key(self):
        self.assertEqual(mi.argsort(['bb', 'a', 'ccc'], key=len), [1, 0, 2])
        self.assertEqual(mi.argsort(['bb', 'a', 'ccc'], key=len, reverse=True), [2, 0, 1])

    def test_consumes_an_iterator_once(self):
        self.assertEqual(mi.argsort(iter([5, 4, 6])), [1, 0, 2])

    def test_options_are_keyword_only(self):
        with self.assertRaises(TypeError):
            mi.argsort([1, 2], len)

    def test_exported_where_the_others_are(self):
        self.assertIn('argsort', more.__all__)
        self.assertIs(mi.argsort, more.argsort)

    def test_typed_and_documented(self):
        with open('more_itertools/more.pyi', encoding='utf-8') as f:
            self.assertIn('def argsort(', f.read())
        with open('docs/api.rst', encoding='utf-8') as f:
            self.assertIn('argsort', f.read())
EOF
python3 -m unittest -q tests.test_chargehand_bench_argsort
