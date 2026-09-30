def load_settings(values):
    """The settings of the program, from what the user wrote. What was not written has its default."""
    timeout = values.get("timeout", 30)
    if timeout <= 0:
        raise ValueError("timeout must be positive")
    return {
        "name": values.get("name", "unnamed"),
        "timeout": timeout,
    }
