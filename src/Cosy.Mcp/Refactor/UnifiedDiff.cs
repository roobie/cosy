using System.Text;
using DiffPlex;

namespace Cosy.Mcp.Refactor;

/// <summary>
/// Renders a git-style unified diff (3 lines of context) between two document texts.
/// Used by read_snapshot (ADR-0010) to project a staged change as a compact, reviewable
/// delta without echoing whole files. Hunk grouping is hand-rolled over DiffPlex's
/// line-level <c>DiffBlock</c>s (which carry the old/new line coordinates directly).
/// </summary>
internal static class UnifiedDiff
{
    private const int Context = 3;

    public static string Render(string oldText, string newText)
    {
        var result = Differ.Instance.CreateLineDiffs(oldText, newText, ignoreWhitespace: false);
        var blocks = result.DiffBlocks;
        if (blocks.Count == 0) return "";

        var oldLines = result.PiecesOld;
        var newLines = result.PiecesNew;
        var sb = new StringBuilder();

        int i = 0;
        while (i < blocks.Count)
        {
            // Merge consecutive blocks whose context windows touch/overlap (gap <= 2*Context).
            int j = i;
            while (j + 1 < blocks.Count
                   && blocks[j + 1].DeleteStartA - (blocks[j].DeleteStartA + blocks[j].DeleteCountA) <= 2 * Context)
            {
                j++;
            }

            var first = blocks[i];
            var last = blocks[j];
            int oldStart = Math.Max(0, first.DeleteStartA - Context);
            int oldEnd = Math.Min(oldLines.Length, last.DeleteStartA + last.DeleteCountA + Context);
            int newStart = Math.Max(0, first.InsertStartB - Context);
            int newEnd = Math.Min(newLines.Length, last.InsertStartB + last.InsertCountB + Context);

            sb.Append("@@ -").Append(oldStart + 1).Append(',').Append(oldEnd - oldStart)
              .Append(" +").Append(newStart + 1).Append(',').Append(newEnd - newStart)
              .Append(" @@\n");

            int oldPos = oldStart;
            int newPos = newStart;
            for (int b = i; b <= j; b++)
            {
                var blk = blocks[b];
                // Leading context (unchanged lines are aligned old/new outside diff blocks).
                while (oldPos < blk.DeleteStartA)
                {
                    sb.Append(' ').Append(oldLines[oldPos]).Append('\n');
                    oldPos++; newPos++;
                }
                for (int k = 0; k < blk.DeleteCountA; k++)
                {
                    sb.Append('-').Append(oldLines[oldPos]).Append('\n');
                    oldPos++;
                }
                for (int k = 0; k < blk.InsertCountB; k++)
                {
                    sb.Append('+').Append(newLines[newPos]).Append('\n');
                    newPos++;
                }
            }
            // Trailing context.
            while (oldPos < oldEnd)
            {
                sb.Append(' ').Append(oldLines[oldPos]).Append('\n');
                oldPos++;
            }

            i = j + 1;
        }
        return sb.ToString();
    }
}
