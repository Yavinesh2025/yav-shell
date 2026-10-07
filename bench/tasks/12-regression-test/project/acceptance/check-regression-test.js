// Acceptance check: the tests in the directory test must pass as the code is now,
// and they must fail for the code as it was, which has the defect.
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = dirname(dirname(fileURLToPath(import.meta.url)));
const runTests = (directory) => spawnSync(process.execPath, ['--test', 'test/*.test.js'], { cwd: directory, encoding: 'utf8' });

// File by file: cpSync with recursive ends Node.js 22 without a word when the path has a character
// outside ASCII, and a task can be run in such a path.
const copyDirectory = (from, to) => {
  mkdirSync(to, { recursive: true });
  for (const entry of readdirSync(from, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      copyDirectory(join(from, entry.name), join(to, entry.name));
    } else {
      copyFileSync(join(from, entry.name), join(to, entry.name));
    }
  }
};

const now = runTests(project);
if (now.status !== 0) {
  console.log('The tests in the directory test do not pass:');
  console.log(now.stdout);
  process.exit(1);
}

const copy = mkdtempSync(join(tmpdir(), 'range-'));
let shown;
try {
  copyDirectory(join(project, 'test'), join(copy, 'test'));
  copyFileSync(join(project, 'package.json'), join(copy, 'package.json'));
  mkdirSync(join(copy, 'src'));
  copyFileSync(join(project, 'acceptance', 'original', 'range.js'), join(copy, 'src', 'range.js'));
  shown = runTests(copy).status !== 0;
} finally {
  rmSync(copy, { recursive: true, force: true });
}

// Only after the copy is gone: process.exit does not run a finally block.
if (!shown) {
  console.log('The tests in the directory test pass for the code that has the defect, so none of them shows it.');
  process.exit(1);
}

console.log('A test shows the defect, and the code passes it.');
