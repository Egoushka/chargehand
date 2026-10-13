set -e
cat > tests/chargehand-bench-example.test.js <<'EOF'
import * as commander from '../index.js';
import { test } from 'node:test';
import assert from 'node:assert/strict';

function program() {
  return new commander.Command().name('prog').option('-a, --alpha', 'first');
}

test('examples close the help under their own heading, in the order added', () => {
  const p = program().example('prog --alpha').example('  prog --alpha --verbose  ');
  const help = p.helpInformation();
  assert.ok(help.includes('\n\nExample usage:\n  $ prog --alpha\n  $ prog --alpha --verbose\n'), help);
  assert.ok(help.trimEnd().endsWith('  $ prog --alpha --verbose'), help);
  assert.ok(help.indexOf('Options:') < help.indexOf('Example usage:'), help);
});

test('example() returns the command for chaining', () => {
  const p = program();
  assert.equal(p.example('prog'), p);
});

test('a command without examples prints the same help as before', () => {
  assert.doesNotMatch(program().helpInformation(), /Example usage/);
});

test('an empty or blank example is refused', () => {
  assert.throws(() => program().example(''), Error);
  assert.throws(() => program().example('   '), Error);
});

test('a subcommand shows its own examples only', () => {
  const root = program().example('prog root');
  const sub = root.command('sub').description('a subcommand').example('prog sub');
  assert.ok(sub.helpInformation().includes('  $ prog sub\n'));
  assert.ok(!sub.helpInformation().includes('prog root'));
  assert.ok(!root.helpInformation().includes('prog sub\n'));
});
EOF
node --test tests/chargehand-bench-example.test.js
grep -Eq '^\s+example\(' typings/index.d.ts
