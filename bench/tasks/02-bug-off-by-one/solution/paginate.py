def page_count(items, per_page):
    """How many pages are needed to show that many items."""
    if per_page <= 0:
        raise ValueError("per_page must be positive")
    return (items + per_page - 1) // per_page
