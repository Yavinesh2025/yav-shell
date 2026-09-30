import unittest

from settings import load_settings


class TimeoutTests(unittest.TestCase):
    def test_the_default_is_thirty_seconds(self):
        self.assertEqual(load_settings({})["timeout"], 30)

    def test_a_timeout_that_is_given_is_used(self):
        self.assertEqual(load_settings({"timeout": 5})["timeout"], 5)

    def test_a_timeout_that_is_not_positive_is_an_error(self):
        for value in (0, -3):
            with self.assertRaises(ValueError):
                load_settings({"timeout": value})

    def test_the_name_is_as_before(self):
        self.assertEqual(load_settings({"name": "x"})["name"], "x")
        self.assertEqual(load_settings({})["name"], "unnamed")


if __name__ == "__main__":
    unittest.main()
