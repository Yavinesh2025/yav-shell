namespace Yav.Tests.Support;

/// <summary>
/// Reported as skipped where the directory of temporary files has no short name of its own: there, what a test
/// of short names compares would be the same text twice, and the test could not fail.
/// </summary>
public sealed class ShortNamesFactAttribute : FactAttribute
{
    public ShortNamesFactAttribute()
    {
        var temporary = Path.GetTempPath();
        if (string.Equals(WindowsNames.Short(temporary), WindowsNames.Long(temporary), StringComparison.OrdinalIgnoreCase))
        {
            Skip = "The directory of temporary files has no short name here, so a short name cannot be compared with a long one.";
        }
    }
}
