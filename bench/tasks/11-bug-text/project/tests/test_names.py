import unittest

from names import initials


class InitialsTests(unittest.TestCase):
    def test_first_letters_in_upper_case(self):
        self.assertEqual(initials("ada lovelace"), "AL")

    def test_blanks_do_not_count(self):
        self.assertEqual(initials("  Ada   King  Lovelace "), "AKL")

    def test_letters_of_any_alphabet(self):
        self.assertEqual(initials("élodie de la croix"), "ÉDLC")
        self.assertEqual(initials("ørjan åse"), "ØÅ")

    def test_a_name_without_words_has_no_initials(self):
        self.assertEqual(initials(""), "")
        self.assertEqual(initials("   "), "")

    def test_tabs_separate_words_as_well(self):
        self.assertEqual(initials("ada\tlovelace"), "AL")


if __name__ == "__main__":
    unittest.main()
