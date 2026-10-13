set -e
cat > tests/chargehand-bench-required-unless.test.js <<'EOF'
import * as commander from '../index.js';
import { test } from 'node:test';
import assert from 'node:assert/strict';

function setup(build) {
  const errors = [];
  const program = new commander.Command();
  program.exitOverride().configureOutput({ writeErr: (s) => errors.push(s), writeOut: () => {} });
  build(program);
  return { program, errors };
}

const token = () => new commander.Option('-t, --token <t>', 'api token').requiredUnless('anonymous');

test('the option is required when the named option is not given', () => {
  const { program, errors } = setup((p) => p.addOption(token()).option('--anonymous'));
  assert.throws(() => program.parse([], { from: 'user' }), { code: 'commander.missingMandatoryOptionValue', exitCode: 1 });
  assert.deepEqual(errors, ["error: required option '-t, --token <t>' not specified unless option '--anonymous' is given\n"]);
});

test('it is not required when the named option is given, or when it is itself', () => {
  for (const argv of [['--anonymous'], ['--token', 'x'], ['--token', 'x', '--anonymous']]) {
    const { program, errors } = setup((p) => p.addOption(token()).option('--anonymous'));
    program.parse(argv, { from: 'user' });
    assert.deepEqual(errors, []);
  }
});

test('two names: either one is enough, and the message lists both', () => {
  const build = (p) =>
    p.addOption(new commander.Option('-t, --token <t>').requiredUnless('anonymous', 'offline')).option('--anonymous').option('--offline');
  for (const argv of [['--anonymous'], ['--offline']]) {
    const { program } = setup(build);
    program.parse(argv, { from: 'user' });
  }
  const { program, errors } = setup(build);
  assert.throws(() => program.parse([], { from: 'user' }), { code: 'commander.missingMandatoryOptionValue' });
  assert.deepEqual(errors, ["error: required option '-t, --token <t>' not specified unless option '--anonymous' or '--offline' is given\n"]);
});

test('a value of the named option from the environment counts', () => {
  process.env.CHARGEHAND_BENCH_ANON = '1';
  try {
    const { program, errors } = setup((p) => p.addOption(token()).addOption(new commander.Option('--anonymous').env('CHARGEHAND_BENCH_ANON')));
    program.parse([], { from: 'user' });
    assert.deepEqual(errors, []);
  } finally {
    delete process.env.CHARGEHAND_BENCH_ANON;
  }
});

test('a default of the named option does not count', () => {
  const { program } = setup((p) =>
    p.addOption(new commander.Option('-t, --token <t>').requiredUnless('mode')).addOption(new commander.Option('-m, --mode <m>').default('plain')),
  );
  assert.throws(() => program.parse([], { from: 'user' }), { code: 'commander.missingMandatoryOptionValue' });
});

test('requiredUnless() returns the option for chaining', () => {
  const option = new commander.Option('-t, --token <t>');
  assert.equal(option.requiredUnless('x'), option);
});
EOF
node --test tests/chargehand-bench-required-unless.test.js
grep -Eq '^\s+requiredUnless\(' typings/index.d.ts
