UNITS = {"s": 1, "m": 60}


def parse_timeout(value):
    """Seconds, from a number or from text such as "45s" or "2m"."""
    if isinstance(value, str):
        text = value.strip()
        factor = 1
        if text and text[-1] in UNITS:
            factor = UNITS[text[-1]]
            text = text[:-1]
        if not text.isdigit():
            raise ValueError(f"'{value}' is not a time")
        value = int(text) * factor
    if value <= 0:
        raise ValueError("timeout must be positive")
    return value


def load_settings(values):
    """The settings of the program, from what the user wrote. What was not written has its default."""
    return {
        "name": values.get("name", "unnamed"),
        "timeout": parse_timeout(values.get("timeout", 30)),
    }
