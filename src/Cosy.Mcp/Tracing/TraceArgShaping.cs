using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cosy.Mcp.Tracing;

/// <summary>
/// D-06/D-12 fail-closed argument-shaping transform (13-CONTEXT.md, 13-RESEARCH.md Deep-Dive Q2 /
/// Pitfall 3). Turns every free-text tool argument into a length -- or, for
/// <c>apply_edits_verified</c>/<c>extract_method</c>'s <c>edits</c> array, a per-edit length plus
/// one structural <c>edits_shape</c> label -- before it reaches <see cref="TraceRecord.Args"/>.
/// Never a hash or truncated preview: 13-RESEARCH.md Q2 names a content-derived fingerprint as an
/// obfuscated proxy for the text it claims to protect, not a middle ground, and this class does
/// not build one.
///
/// The rule is an ALLOWLIST, not a denylist, and the direction is the point (same reasoning
/// ADR-0017 already applied to the public-mirror allowlist): a denylist of known text keys fails
/// OPEN -- a tool shipped next year with a new text parameter leaks verbatim because nobody
/// remembered to add it. This allowlist fails CLOSED -- an unrecognised key is shaped by default
/// (<see cref="ShapeValue"/>'s final branch), and
/// <c>TraceArgShapingTests.Reflection_EveryStringToolParameter_IsClassified</c> is the guard that
/// keeps <see cref="VerbatimKeys"/> / <see cref="ShapedTextKeys"/> in sync with the shipped tool
/// signatures, so this stays true of the NEXT tool, not just today's eighteen.
///
/// The line D-12 draws is between arguments that WRITE text into source (shaped) and arguments
/// that IDENTIFY existing code (kept verbatim). <c>symbol</c> stays verbatim even though it is a
/// string, because the trace record already echoes <c>resolved_symbol.display_name</c> /
/// <c>doc_id</c> verbatim from the RESPONSE side (ADR-0007 §2.1, unchanged by D-06/D-12 -- those
/// are argument-scoped); shaping the argument would destroy the exact evidence the known
/// fuzzy/metadata-resolver contamination limitation needs (a <c>symbol_not_found</c> count is
/// uninterpretable without knowing what was asked for) while reducing nothing.
///
/// SHAPE LABELS ARE NOT SEMANTIC CLASSIFICATIONS (D-10). <c>rename_shaped</c> means N edits of
/// matching size, not N edits of the SAME identifier; <c>extract_shaped</c> cannot confirm the
/// shrunk call site actually references the inserted declaration. Without <c>new_text</c>,
/// structural classification is a ceiling on what can be known, not a semantic proof -- state this
/// limitation at every site that reads an <c>edits_shape</c> label, not only here.
/// </summary>
public static class TraceArgShaping
{
    /// <summary>
    /// Arguments that IDENTIFY existing code or carry structural/coordinate data, rather than
    /// WRITE text into it -- kept verbatim regardless of their JSON value kind. Derived by
    /// enumerating every <c>[McpServerTool]</c> method's parameter list across
    /// <c>src/Cosy.Mcp/Tools/</c> (enumerated 2026-09-02 for this plan; the completed
    /// key-to-classification table is recorded in 13-02-SUMMARY.md).
    /// <c>"mode"</c> and <c>"readSnapshotId"</c> are seeded here per 13-02-PLAN.md Task 1's action
    /// text even though neither is a parameter on the surface shipped as of this plan -- a harmless
    /// extra <see cref="HashSet{T}"/> entry today, and pre-emptively correct if a future tool
    /// reuses either name for an identify-only argument.
    /// </summary>
    public static readonly IReadOnlySet<string> VerbatimKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "symbol", "file", "path", "pathGlob", "nameGlob", "project",
        "snapshotId", "fromSnapshotId", "verify",
        "span", "start", "end", "max", "maxChars", "timeoutMs",
        "edits", // the ARRAY key itself; per-element shaping is ShapeEdits, not this table
        "mode", "readSnapshotId",
    };

    /// <summary>
    /// Arguments that WRITE free text into source, or an agent-composed query expression over it
    /// -- replaced with a <see cref="ShapedText"/> (length only). Keys here are WIRE keys, not the
    /// prose names ADR-0007/D-12 use: the MCP SDK binds request parameters by C# identifier (same
    /// precedent as <c>timeoutMs</c> staying camelCase, Phase 8 Plan 07), so ADR-0007's
    /// <c>rename.new_name</c> is wire key <c>"newName"</c>, not <c>"new_name"</c>. The one
    /// exception is <c>edits[].new_text</c>: <c>ApplyEditsVerifiedTool.EditRequest</c> carries an
    /// explicit <c>[JsonPropertyName("new_text")]</c> override on that record property, so inside
    /// an edit element the wire key really is the snake_case <c>"new_text"</c> verbatim.
    /// </summary>
    public static readonly IReadOnlySet<string> ShapedTextKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "new_text",       // apply_edits_verified / extract_method edits[] element (explicit JsonPropertyName)
        "snippet",        // compile_check.snippet
        "newName",        // rename's new-identifier arg -- wire key for ADR-0007's "rename.new_name"
        "pattern",        // find_text.pattern
        "newMethodName",  // extract_method's new-identifier arg -- same "writes text" reasoning as newName
        "filter",         // run_tests's VSTest filter expression -- agent-composed query text, not an id
    };

    /// <summary>
    /// Shapes one tool call's arguments. Returns null when <paramref name="args"/> is null (the
    /// no-op path TraceSink.Emit already has for a null/empty COSY_TRACE_PATH is unchanged).
    /// <paramref name="toolName"/> is accepted for future per-tool disambiguation (e.g. two tools
    /// reusing the same argument key with different semantics) but is not read by any rule today --
    /// every current rule is keyed on the argument name alone.
    /// </summary>
    public static Dictionary<string, object?>? Shape(IDictionary<string, JsonElement>? args, string toolName)
    {
        if (args is null) return null;

        var shaped = new Dictionary<string, object?>(args.Count + 1);
        foreach (var (key, value) in args)
        {
            // edits[] gets a dedicated per-element transform (drop new_text, add
            // new_text_length, keep file/span) plus a derived edits_shape sibling key -- this is
            // the one level of descent the shipped surface needs; nothing else recurses.
            if (key == "edits" && value.ValueKind == JsonValueKind.Array)
            {
                shaped[key] = ShapeEdits(value, out var editsShape);
                shaped["edits_shape"] = editsShape;
                continue;
            }

            shaped[key] = ShapeValue(key, value);
        }
        return shaped;
    }

    private static object? ShapeValue(string key, JsonElement value)
    {
        // Rule 1: no free text in a number/bool/null -- keep verbatim regardless of key.
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
            return value;

        // Rule 3: explicit identity allowlist -- kept verbatim regardless of value kind (e.g.
        // `span` is a {start,end} object, not a string, and stays verbatim as a whole).
        if (VerbatimKeys.Contains(key))
            return value;

        // Rule 2 (known text key) / Rule 4 (unknown key, fail-closed default): both shape a
        // string value identically. ShapedTextKeys exists as the recorded, audited set the
        // reflection drift guard checks membership against -- it does not change this branch's
        // behavior, since an unrecognised string key must shape the same way a known one does.
        if (value.ValueKind == JsonValueKind.String)
            return new ShapedText(value.GetString()?.Length ?? 0);

        // Rule 4, non-string case: a future tool's object/array-valued argument this table has
        // never seen. No key on the shipped surface reaches this branch today -- `edits` is the
        // only array-valued top-level argument, and it is intercepted above before ShapeValue
        // ever sees it. Kept so such an argument is never boxed through verbatim by omission.
        return new ShapedUnknown(value.ValueKind.ToString());
    }

    /// <summary>
    /// Per-element transform for the <c>edits</c> array: keeps <c>file</c> and <c>span</c>
    /// verbatim, drops <c>new_text</c>, adds <c>new_text_length</c>. Also computes the one
    /// per-call <c>edits_shape</c> label from the resulting (replaced_length, new_text_length)
    /// pairs, implementing 13-RESEARCH.md Q2's table.
    /// </summary>
    private static List<object?> ShapeEdits(JsonElement editsArray, out string editsShape)
    {
        var shapedEdits = new List<object?>();
        var summaries = new List<EditSummary>();

        foreach (var edit in editsArray.EnumerateArray())
        {
            var perEdit = new Dictionary<string, object?>();
            int replacedLength = 0, newTextLength = 0;
            string? file = null;

            foreach (var prop in edit.EnumerateObject())
            {
                if (prop.NameEquals("new_text"))
                {
                    newTextLength = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()!.Length
                        : 0;
                    perEdit["new_text_length"] = newTextLength;
                    continue;
                }

                if (prop.NameEquals("span")
                    && prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("start", out var startEl) && startEl.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetProperty("end", out var endEl) && endEl.ValueKind == JsonValueKind.Number)
                {
                    replacedLength = endEl.GetInt32() - startEl.GetInt32();
                }

                if (prop.NameEquals("file") && prop.Value.ValueKind == JsonValueKind.String)
                    file = prop.Value.GetString();

                // file, span, and any other key an EditRequest ever grows stay verbatim -- D-06's
                // redaction is scoped to new_text only (13-RESEARCH.md Pitfall 3 is the trap of
                // scoping this too narrowly; it is not a trap of scoping it too broadly).
                perEdit[prop.Name] = prop.Value;
            }

            shapedEdits.Add(perEdit);
            summaries.Add(new EditSummary(file, replacedLength, newTextLength));
        }

        editsShape = ClassifyEditsShape(summaries);
        return shapedEdits;
    }

    private readonly record struct EditSummary(string? File, int ReplacedLength, int NewTextLength)
    {
        public int Delta => NewTextLength - ReplacedLength;
    }

    /// <summary>
    /// 13-RESEARCH.md Deep-Dive Q2's structural-shape table, implemented in the priority order
    /// the plan's action text specifies: rename/extract/inline are checked before the simpler
    /// single/pure-insertion/pure-deletion shapes, because a single edit or an all-zero-length
    /// edit set would otherwise trivially satisfy "constant replaced_length and constant
    /// new_text_length" and be misclassified as rename_shaped.
    /// </summary>
    private static string ClassifyEditsShape(IReadOnlyList<EditSummary> edits)
    {
        if (edits.Count >= 2
            && edits.Select(e => e.ReplacedLength).Distinct().Count() == 1
            && edits.Select(e => e.NewTextLength).Distinct().Count() == 1)
            return "rename_shaped";

        if (edits.Count == 2 && edits[0].File == edits[1].File)
        {
            if (IsExtractPair(edits[0], edits[1])) return "extract_shaped";
            if (IsInlinePair(edits[0], edits[1])) return "inline_shaped";
        }

        if (edits.Count == 1) return "single_edit";
        if (edits.Count > 0 && edits.All(e => e.ReplacedLength == 0)) return "pure_insertion";
        if (edits.Count > 0 && edits.All(e => e.NewTextLength == 0)) return "pure_deletion";
        return "unclassified";
    }

    // extract_shaped: a call-site shrink (delta<0, a real non-zero-width edit) paired with a
    // zero-width insertion of a new declaration (replaced_length==0, new_text_length>0).
    private static bool IsExtractPair(EditSummary a, EditSummary b)
    {
        static bool IsShrink(EditSummary x) => x.ReplacedLength > 0 && x.Delta < 0;
        static bool IsInsertion(EditSummary x) => x.ReplacedLength == 0 && x.NewTextLength > 0;
        return (IsShrink(a) && IsInsertion(b)) || (IsShrink(b) && IsInsertion(a));
    }

    // inline_shaped: the mirror of extract_shaped -- a call site replaced by grown inlined text
    // (delta>0, a real non-zero-width edit) paired with a deletion of the old declaration
    // (replaced_length>0, new_text_length==0). Neither component is zero-width, which is exactly
    // what distinguishes this from extract_shaped's zero-width insertion component.
    private static bool IsInlinePair(EditSummary a, EditSummary b)
    {
        static bool IsGrow(EditSummary x) => x.ReplacedLength > 0 && x.Delta > 0;
        static bool IsDeletion(EditSummary x) => x.ReplacedLength > 0 && x.NewTextLength == 0;
        return (IsGrow(a) && IsDeletion(b)) || (IsGrow(b) && IsDeletion(a));
    }
}

/// <summary>
/// Replacement for a shaped free-text argument -- length only, never the value (D-06/D-12).
/// A named public record (rather than an anonymous type) so unit tests in the
/// <c>Cosy.Mcp.Tests</c> assembly can assert against <see cref="Len"/> directly instead of
/// round-tripping through JSON; same visibility precedent as <see cref="TraceResolvedSymbol"/>.
/// </summary>
public sealed record ShapedText([property: JsonPropertyName("len")] int Len);

/// <summary>
/// Fail-closed placeholder for a non-string, non-verbatim argument value this table has never
/// seen (Rule 4's non-string case). No key on the shipped surface reaches this today -- kept so a
/// future tool's object/array-valued argument is never boxed through verbatim by omission.
/// </summary>
public sealed record ShapedUnknown([property: JsonPropertyName("kind")] string Kind);
