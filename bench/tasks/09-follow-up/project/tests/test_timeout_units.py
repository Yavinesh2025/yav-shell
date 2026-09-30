import unittest

from settings import load_settings


class TimeoutUnitTests(unittest.TestCase):
    def test_seconds_can_be_written_with_their_unit(self):
        self.assertEqual(load_settings({"timeout": "45s"})["timeout"], 45)

    def test_minutes_are_turned_into_seconds(self):
        self.assertEqual(load_settings({"timeout": "2m"})["timeout"], 120)

    def test_a_number_as_text_is_seconds(self):
        self.assertEqual(load_settings({"timeout": "7"})["timeout"], 7)

    def test_what_is_not_a_time_is_an_error(self):
        for value in ("soon", "5h", "", "-1s"):
            with self.assertRaises(ValueError):
                load_settings({"timeout": value})


if __name__ == "__main__":
    unittest.main()
