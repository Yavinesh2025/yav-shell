def load_settings(values):
    """The settings of the program, from what the user wrote. What was not written has its default."""
    return {
        "name": values.get("name", "unnamed"),
    }
