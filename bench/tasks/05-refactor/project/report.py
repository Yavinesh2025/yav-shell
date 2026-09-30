def build_report(lines):
    """Lines look like "name,amount". Returns the sum of the amounts for each name, names in alphabetical order."""
    totals = {}
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split(",")
        name = parts[0].strip()
        amount = int(parts[1].strip())
        if name in totals:
            totals[name] = totals[name] + amount
        else:
            totals[name] = amount
    return dict(sorted(totals.items()))
