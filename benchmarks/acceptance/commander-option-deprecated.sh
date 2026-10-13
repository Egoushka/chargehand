set -e
cat > tests/chargehand-bench-deprecated.test.js <<'EOF'
import * as commander from '../index.js';
import { test } from 'node:test';
import assert from 'node:assert/strict';

function setup() {
  const errors = [];
  const program = new commander.Command();
  program.exitOverride().configureOutput({ writeErr: (s) => errors.push(s), writeOut: () => {} });
  return { program, errors };
}

test('warns when the deprecated option is used and still stores its value', () => {
  const { program, errors } = setup();
  program.addOption(new commander.Option('-o, --old <value>', 'the old way').deprecated('use --new'));
  program.option('--new <value>');
  program.parse(['--old', 'x'], { from: 'user' });
  assert.deepEqual(errors, ["warning: option '-o, --old <value>' is deprecated: use --new\n"]);
  assert.equal(program.opts().old, 'x');
});

test('warns for a boolean option too', () => {
  const { program, errors } = setup();
  program.addOption(new commander.Option('--legacy', 'legacy mode').deprecated('gone in 3.0'));
  program.parse(['--legacy'], { from: 'user' });
  assert.deepEqual(errors, ["warning: option '--legacy' is deprecated: gone in 3.0\n"]);
  assert.equal(program.opts().legacy, true);
});

test('says nothing when the deprecated option is not used', () => {
  const { program, errors } = setup();
  program.addOption(new commander.Option('--legacy', 'legacy mode').deprecated('gone in 3.0'));
  program.option('--fresh');
  program.parse(['--fresh'], { from: 'user' });
  assert.deepEqual(errors, []);
});

test('an option that is not deprecated is unchanged', () => {
  const { program, errors } = setup();
  program.option('-a, --alpha <v>', 'first letter');
  program.parse(['-a', '1'], { from: 'user' });
  assert.deepEqual(errors, []);
  assert.equal(program.opts().alpha, '1');
});

test('the help marks the option as deprecated', () => {
  const { program } = setup();
  program.addOption(new commander.Option('--legacy', 'legacy mode').deprecated('gone in 3.0'));
  program.option('--fresh', 'new mode');
  const help = program.helpInformation();
  assert.match(help, /legacy mode \(deprecated\)/);
  assert.doesNotMatch(help, /new mode \(deprecated\)/);
});

test('deprecated() returns the option for chaining', () => {
  const option = new commander.Option('--legacy');
  assert.equal(option.deprecated('x'), option);
});
EOF
node --test tests/chargehand-bench-deprecated.test.js
grep -Eq '^\s+deprecated\(' typings/index.d.ts
