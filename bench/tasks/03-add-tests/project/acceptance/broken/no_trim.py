import re


def slugify(text):
    """Turns a title into the part of an address: lower case, words joined by single hyphens."""
    lowered = text.lower()
    hyphenated = re.sub(r"[^a-z0-9]+", "-", lowered)
    return hyphenated
