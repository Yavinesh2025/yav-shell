def parse_line(line):
    """The name and the amount of a line that looks like "name,amount"."""
    name, amount = line.split(",")
    return name.strip(), int(amount.strip())


def summarize(records):
    """The sum of the amounts for each name, names in alphabetical order."""
    totals = {}
    for name, amount in records:
        totals[name] = totals.get(name, 0) + amount
    return dict(sorted(totals.items()))


def build_report(lines):
    """Lines look like "name,amount". Returns the sum of the amounts for each name, names in alphabetical order."""
    records = []
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        records.append(parse_line(line))
    return summarize(records)
