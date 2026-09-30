def initials(name):
    """The first letters of the words of a name."""
    letters = ""
    for word in name.split(" "):
        letter = word[0]
        if "a" <= letter <= "z":
            letter = chr(ord(letter) - 32)
        letters += letter
    return letters
