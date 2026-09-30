import unittest

from greeting import greet


class GreetingTests(unittest.TestCase):
    def test_greets_by_name(self):
        self.assertEqual(greet("Ada"), "Hello, Ada.")

    def test_ignores_blanks_around_the_name(self):
        self.assertEqual(greet("  Ada  "), "Hello, Ada.")

    def test_keeps_blanks_inside_the_name(self):
        self.assertEqual(greet("Ada Lovelace"), "Hello, Ada Lovelace.")


if __name__ == "__main__":
    unittest.main()
