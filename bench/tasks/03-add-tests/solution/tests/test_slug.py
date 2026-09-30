import unittest

from slug import slugify


class SlugifyTests(unittest.TestCase):
    def test_lowercases(self):
        self.assertEqual(slugify("Hello World"), "hello-world")

    def test_replaces_what_is_neither_letter_nor_digit_by_one_hyphen(self):
        self.assertEqual(slugify("a  b -- c, d"), "a-b-c-d")

    def test_removes_hyphens_at_both_ends(self):
        self.assertEqual(slugify("  ...Hello!  "), "hello")

    def test_keeps_digits(self):
        self.assertEqual(slugify("Version 2.0"), "version-2-0")


if __name__ == "__main__":
    unittest.main()
