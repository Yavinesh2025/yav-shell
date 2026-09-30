import unittest

import report


class BuildReportTests(unittest.TestCase):
    def test_adds_up_by_name_in_alphabetical_order(self):
        lines = ["pears, 2", "apples,1", "# a comment", "", "pears,3"]
        self.assertEqual(list(report.build_report(lines).items()), [("apples", 1), ("pears", 5)])

    def test_nothing_gives_nothing(self):
        self.assertEqual(report.build_report([]), {})


class PartsTests(unittest.TestCase):
    def test_a_line_is_a_name_and_an_amount(self):
        self.assertEqual(report.parse_line(" pears , 2 "), ("pears", 2))

    def test_records_are_added_up_by_name(self):
        self.assertEqual(report.summarize([("b", 1), ("a", 2), ("b", 3)]), {"a": 2, "b": 4})

    def test_the_report_is_built_from_the_parts(self):
        calls = []
        original = report.parse_line
        report.parse_line = lambda line: calls.append(line) or original(line)
        try:
            report.build_report(["a,1", "b,2"])
        finally:
            report.parse_line = original
        self.assertEqual(calls, ["a,1", "b,2"])


if __name__ == "__main__":
    unittest.main()
