// Acceptance check: the tests in the directory test must pass as the code is now,
// and they must fail for the code as it was, which has the defect.
import { spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = dirname(dirname(fileURLToPath(import.meta.url)));
const runTests = (directory) => spawnSync(process.execPath, ['--test', 'test/*.test.js'], { cwd: directory, encoding: 'utf8' });

const now = runTests(project);
if (now.status !== 0) {
  console.log('The tests in the directory test do not pass:');
  console.log(now.stdout);
  process.exit(1);
}

const copy = mkdtempSync(join(tmpdir(), 'range-'));
try {
  cpSync(join(project, 'test'), join(copy, 'test'), { recursive: true });
  cpSync(join(project, 'package.json'), join(copy, 'package.json'));
  mkdirSync(join(copy, 'src'));
  cpSync(join(project, 'acceptance', 'original', 'range.js'), join(copy, 'src', 'range.js'));
  if (runTests(copy).status === 0) {
    console.log('The tests in the directory test pass for the code that has the defect, so none of them shows it.');
    process.exit(1);
  }
} finally {
  rmSync(copy, { recursive: true, force: true });
}

console.log('A test shows the defect, and the code passes it.');
