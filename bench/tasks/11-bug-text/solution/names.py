def initials(name):
    """The first letters of the words of a name."""
    return "".join(word[0].upper() for word in name.split())
