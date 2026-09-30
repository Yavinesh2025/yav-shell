import unittest

from paginate import page_count


class PageCountTests(unittest.TestCase):
    def test_nothing_needs_no_page(self):
        self.assertEqual(page_count(0, 10), 0)

    def test_a_full_page_is_one_page(self):
        self.assertEqual(page_count(10, 10), 1)

    def test_one_item_more_needs_another_page(self):
        self.assertEqual(page_count(11, 10), 2)

    def test_a_single_item_needs_a_page(self):
        self.assertEqual(page_count(1, 10), 1)

    def test_a_page_size_that_is_not_positive_is_an_error(self):
        for size in (0, -1):
            with self.assertRaises(ValueError):
                page_count(5, size)


if __name__ == "__main__":
    unittest.main()
