set -e
cat > tests/chargehand-bench-range.test.js <<'EOF'
import * as commander from '../index.js';
import { test } from 'node:test';
import assert from 'node:assert/strict';

function setup(configure) {
  const errors = [];
  const program = new commander.Command();
  program.exitOverride().configureOutput({ writeErr: (s) => errors.push(s), writeOut: () => {} });
  program.addOption(configure(new commander.Option('-n, --num <n>', 'a count')));
  return { program, errors };
}

test('stores a number inside the range, ends included', () => {
  for (const [text, value] of [['7', 7], ['1', 1], ['10', 10], ['2.5', 2.5]]) {
    const { program } = setup((o) => o.range(1, 10));
    program.parse(['--num', text], { from: 'user' });
    assert.strictEqual(program.opts().num, value);
  }
});

test('rejects a value outside the range or not a number', () => {
  for (const text of ['0', '11', '-3', 'abc', '5x']) {
    const { program, errors } = setup((o) => o.range(1, 10));
    assert.throws(() => program.parse(['--num', text], { from: 'user' }), { code: 'commander.invalidArgument', exitCode: 1 });
    assert.deepEqual(errors, [`error: option '-n, --num <n>' argument '${text}' is invalid. Expected a number from 1 to 10.\n`]);
  }
});

test('applies to a value from the environment with the environment wording', () => {
  process.env.CHARGEHAND_BENCH_NUM = '99';
  try {
    const { program, errors } = setup((o) => o.env('CHARGEHAND_BENCH_NUM').range(1, 10));
    assert.throws(() => program.parse([], { from: 'user' }), { code: 'commander.invalidArgument', exitCode: 1 });
    assert.deepEqual(errors, ["error: option '-n, --num <n>' value '99' from env 'CHARGEHAND_BENCH_NUM' is invalid. Expected a number from 1 to 10.\n"]);
  } finally {
    delete process.env.CHARGEHAND_BENCH_NUM;
  }
});

test('a default inside no range check is left alone, and a missing option stays undefined', () => {
  const { program } = setup((o) => o.range(1, 10));
  program.parse([], { from: 'user' });
  assert.strictEqual(program.opts().num, undefined);
});

test('range() returns the option for chaining', () => {
  const option = new commander.Option('-n, --num <n>');
  assert.equal(option.range(1, 2), option);
});
EOF
node --test tests/chargehand-bench-range.test.js
grep -Eq '^\s+range\(' typings/index.d.ts
