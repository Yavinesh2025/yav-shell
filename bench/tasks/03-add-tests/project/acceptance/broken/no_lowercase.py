import re


def slugify(text):
    """Turns a title into the part of an address: lower case, words joined by single hyphens."""
    lowered = text
    hyphenated = re.sub(r"[^A-Za-z0-9]+", "-", lowered)
    return hyphenated.strip("-")
