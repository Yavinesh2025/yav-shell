import sys


def main(arguments):
    """Counts the words of the text that is given as arguments. Returns what is printed."""
    text = " ".join(arguments)
    return str(len(text.split()))


if __name__ == "__main__":
    print(main(sys.argv[1:]))
