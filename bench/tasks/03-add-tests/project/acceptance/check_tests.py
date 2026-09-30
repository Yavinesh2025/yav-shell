"""Acceptance check: the tests in tests/test_slug.py must pass for slug.py and fail for each broken variant."""
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)


def run_tests(directory):
    return subprocess.run(
        [sys.executable, "-m", "unittest", "discover", "-s", "tests", "-t", "."],
        cwd=directory, capture_output=True, text=True)


def main():
    tests = os.path.join(PROJECT, "tests", "test_slug.py")
    if not os.path.exists(tests):
        print("tests/test_slug.py does not exist")
        return 1

    result = run_tests(PROJECT)
    if result.returncode != 0 or "Ran 0 tests" in result.stderr:
        print("The tests do not pass for the implementation as it is:")
        print(result.stderr)
        return 1

    failures = 0
    for name in sorted(os.listdir(os.path.join(HERE, "broken"))):
        with tempfile.TemporaryDirectory() as copy:
            shutil.copytree(os.path.join(PROJECT, "tests"), os.path.join(copy, "tests"))
            shutil.copy(os.path.join(HERE, "broken", name), os.path.join(copy, "slug.py"))
            if run_tests(copy).returncode == 0:
                print(f"The tests did not notice the defect in {name}")
                failures += 1

    print("The tests notice every defect." if failures == 0 else f"{failures} defect(s) went unnoticed.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
