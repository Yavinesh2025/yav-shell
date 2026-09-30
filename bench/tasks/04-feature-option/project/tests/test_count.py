import subprocess
import sys
import unittest

from count import main


class CountTests(unittest.TestCase):
    def test_counts_words(self):
        self.assertEqual(main(["one", "two  three"]), "3")

    def test_counts_characters_when_asked_to(self):
        self.assertEqual(main(["--chars", "one", "two"]), "7")

    def test_the_option_may_follow_the_text(self):
        self.assertEqual(main(["one", "two", "--chars"]), "7")

    def test_no_text_counts_as_nothing(self):
        self.assertEqual(main([]), "0")
        self.assertEqual(main(["--chars"]), "0")

    def test_an_option_that_is_not_known_ends_with_exit_code_2(self):
        result = subprocess.run([sys.executable, "count.py", "--lines", "x"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)


if __name__ == "__main__":
    unittest.main()
