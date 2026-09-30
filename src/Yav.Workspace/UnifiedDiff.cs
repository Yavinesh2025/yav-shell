using System.Text;

namespace Yav.Workspace;

/// <summary>
/// Produces unified diffs locally, without starting a process and without a model. Lines keep their own
/// line endings, so a change from LF to CRLF is visible as a change.
/// </summary>
public static class UnifiedDiff
{
    private const int BinaryProbeLength = 8000;

    // Above this edit distance the exact algorithm costs too much memory; the file is then shown as replaced.
    private const int MaxEditDistance = 2000;

    /// <summary>Content with a null byte near the start is treated as binary, the rule Git uses.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> content)
    {
        var probe = content.Length > BinaryProbeLength ? content[..BinaryProbeLength] : content;
        return probe.IndexOf((byte)0) >= 0;
    }

    public static string Create(string oldLabel, string newLabel, string oldText, string newText, int context = 3)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var before = SplitLines(oldText);
        var after = SplitLines(newText);
        var operations = Compare(before, after);

        var builder = new StringBuilder();
        builder.Append("--- ").Append(oldLabel).Append('\n');
        builder.Append("+++ ").Append(newLabel).Append('\n');

        var index = 0;
        while (index < operations.Count)
        {
            // Find the next change.
            while (index < operations.Count && operations[index].Kind == OperationKind.Keep)
            {
                index++;
            }

            if (index >= operations.Count)
            {
                break;
            }

            var start = Math.Max(0, index - context);
            var end = index;
            var lastChange = index;
            while (end < operations.Count)
            {
                if (operations[end].Kind != OperationKind.Keep)
                {
                    lastChange = end;
                    end++;
                    continue;
                }

                // A run of unchanged lines longer than two contexts separates two hunks.
                var run = 0;
                while (end + run < operations.Count && operations[end + run].Kind == OperationKind.Keep)
                {
                    run++;
                }

                if (end + run >= operations.Count || run > context * 2)
                {
                    break;
                }

                end += run;
            }

            var stop = Math.Min(operations.Count, lastChange + 1 + context);
            AppendHunk(builder, operations, start, stop, before, after);
            index = stop;
        }

        return builder.ToString();
    }

    private static void AppendHunk(StringBuilder builder, List<Operation> operations, int start, int stop, List<string> before, List<string> after)
    {
        var oldStart = 0;
        var newStart = 0;
        for (var i = 0; i < start; i++)
        {
            if (operations[i].Kind != OperationKind.Insert)
            {
                oldStart++;
            }

            if (operations[i].Kind != OperationKind.Delete)
            {
                newStart++;
            }
        }

        var oldCount = 0;
        var newCount = 0;
        for (var i = start; i < stop; i++)
        {
            if (operations[i].Kind != OperationKind.Insert)
            {
                oldCount++;
            }

            if (operations[i].Kind != OperationKind.Delete)
            {
                newCount++;
            }
        }

        builder.Append("@@ -")
            .Append(oldCount == 0 ? oldStart : oldStart + 1).Append(',').Append(oldCount)
            .Append(" +")
            .Append(newCount == 0 ? newStart : newStart + 1).Append(',').Append(newCount)
            .Append(" @@\n");

        for (var i = start; i < stop; i++)
        {
            var operation = operations[i];
            var line = operation.Kind == OperationKind.Insert ? after[operation.NewIndex] : before[operation.OldIndex];
            builder.Append(operation.Kind switch
            {
                OperationKind.Insert => '+',
                OperationKind.Delete => '-',
                _ => ' ',
            });
            builder.Append(line);
            if (!line.EndsWith('\n'))
            {
                builder.Append("\n\\ No newline at end of file\n");
            }
        }
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private enum OperationKind
    {
        Keep,
        Delete,
        Insert,
    }

    private readonly record struct Operation(OperationKind Kind, int OldIndex, int NewIndex);

    /// <summary>Myers' shortest edit script, applied to the part between the common beginning and ending.</summary>
    private static List<Operation> Compare(List<string> before, List<string> after)
    {
        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < before.Count - prefix
            && suffix < after.Count - prefix
            && string.Equals(before[before.Count - 1 - suffix], after[after.Count - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var operations = new List<Operation>(before.Count + after.Count);
        for (var i = 0; i < prefix; i++)
        {
            operations.Add(new Operation(OperationKind.Keep, i, i));
        }

        var oldLength = before.Count - prefix - suffix;
        var newLength = after.Count - prefix - suffix;
        var middle = Middle(before, after, prefix, oldLength, newLength);
        operations.AddRange(middle);

        for (var i = 0; i < suffix; i++)
        {
            operations.Add(new Operation(OperationKind.Keep, before.Count - suffix + i, after.Count - suffix + i));
        }

        return operations;
    }

    private static List<Operation> Middle(List<string> before, List<string> after, int offset, int oldLength, int newLength)
    {
        var result = new List<Operation>(oldLength + newLength);
        if (oldLength == 0)
        {
            for (var i = 0; i < newLength; i++)
            {
                result.Add(new Operation(OperationKind.Insert, offset, offset + i));
            }

            return result;
        }

        if (newLength == 0)
        {
            for (var i = 0; i < oldLength; i++)
            {
                result.Add(new Operation(OperationKind.Delete, offset + i, offset));
            }

            return result;
        }

        var max = Math.Min(oldLength + newLength, MaxEditDistance);
        var width = (2 * max) + 1;
        var frontier = new int[width];
        var history = new List<int[]>();
        var solved = false;
        var distance = 0;

        for (var d = 0; d <= max && !solved; d++)
        {
            // Only the diagonals -(d+1)..(d+1) can be read at step d, so only those are kept.
            var kept = new int[(2 * d) + 3];
            for (var k = -(d + 1); k <= d + 1; k++)
            {
                var source = max + k;
                kept[k + d + 1] = source >= 0 && source < width ? frontier[source] : 0;
            }

            history.Add(kept);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                var below = max + k - 1 >= 0 ? frontier[max + k - 1] : 0;
                var above = max + k + 1 < width ? frontier[max + k + 1] : 0;
                if (k == -d || (k != d && below < above))
                {
                    x = above;
                }
                else
                {
                    x = below + 1;
                }

                var y = x - k;
                while (x < oldLength && y < newLength && string.Equals(before[offset + x], after[offset + y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                frontier[max + k] = x;
                if (x >= oldLength && y >= newLength)
                {
                    solved = true;
                    distance = d;
                    break;
                }
            }
        }

        if (!solved)
        {
            // Too different to align line by line within the memory bound: show the region as replaced.
            for (var i = 0; i < oldLength; i++)
            {
                result.Add(new Operation(OperationKind.Delete, offset + i, offset));
            }

            for (var i = 0; i < newLength; i++)
            {
                result.Add(new Operation(OperationKind.Insert, offset + oldLength, offset + i));
            }

            return result;
        }

        var reversed = new List<Operation>(oldLength + newLength);
        var currentX = oldLength;
        var currentY = newLength;
        for (var d = distance; d > 0; d--)
        {
            var previous = history[d];
            var center = d + 1;
            var k = currentX - currentY;
            int previousK;
            if (k == -d || (k != d && previous[center + k - 1] < previous[center + k + 1]))
            {
                previousK = k + 1;
            }
            else
            {
                previousK = k - 1;
            }

            var previousX = previous[center + previousK];
            var previousY = previousX - previousK;

            while (currentX > previousX && currentY > previousY)
            {
                currentX--;
                currentY--;
                reversed.Add(new Operation(OperationKind.Keep, offset + currentX, offset + currentY));
            }

            if (currentX == previousX)
            {
                currentY--;
                reversed.Add(new Operation(OperationKind.Insert, offset + currentX, offset + currentY));
            }
            else
            {
                currentX--;
                reversed.Add(new Operation(OperationKind.Delete, offset + currentX, offset + currentY));
            }
        }

        while (currentX > 0 && currentY > 0)
        {
            currentX--;
            currentY--;
            reversed.Add(new Operation(OperationKind.Keep, offset + currentX, offset + currentY));
        }

        reversed.Reverse();

        // Within a changed block, removals are listed before additions, as diff tools do.
        var ordered = new List<Operation>(reversed.Count);
        var i2 = 0;
        while (i2 < reversed.Count)
        {
            if (reversed[i2].Kind == OperationKind.Keep)
            {
                ordered.Add(reversed[i2]);
                i2++;
                continue;
            }

            var blockStart = i2;
            while (i2 < reversed.Count && reversed[i2].Kind != OperationKind.Keep)
            {
                i2++;
            }

            for (var j = blockStart; j < i2; j++)
            {
                if (reversed[j].Kind == OperationKind.Delete)
                {
                    ordered.Add(reversed[j]);
                }
            }

            for (var j = blockStart; j < i2; j++)
            {
                if (reversed[j].Kind == OperationKind.Insert)
                {
                    ordered.Add(reversed[j]);
                }
            }
        }

        return ordered;
    }
}
