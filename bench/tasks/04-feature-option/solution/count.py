import sys

OPTIONS = ("--chars",)


def main(arguments):
    """Counts the words, or with --chars the characters, of the text that is given as arguments."""
    unknown = [a for a in arguments if a.startswith("--") and a not in OPTIONS]
    if unknown:
        raise SystemExit(2)

    text = " ".join(a for a in arguments if a not in OPTIONS)
    if "--chars" in arguments:
        return str(len(text))
    return str(len(text.split()))


if __name__ == "__main__":
    print(main(sys.argv[1:]))
