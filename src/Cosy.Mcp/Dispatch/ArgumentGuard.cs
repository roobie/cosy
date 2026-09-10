using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Dispatch;

/// <summary>
/// The parameter names one tool advertises, projected out of its own JSON Schema
/// (<c>McpServerTool.ProtocolTool.InputSchema</c>) rather than out of its C# signature, so the
/// names this type reports and the names <c>tools/list</c> told the caller cannot drift apart.
/// </summary>
public sealed record ToolArgumentSchema(
    IReadOnlyList<string> Required,
    IReadOnlyList<string> Accepted,
    bool IsDeclared)
{
    /// <summary>
    /// Phase 12.3 Task 3 (D-06): for each top-level property whose schema declares an array type
    /// with an object <c>items</c> schema, the wire keys that <c>items.properties</c> declares —
    /// keyed by the top-level property name. Schema-derived, never tool-name-derived: this reads
    /// the tool's own advertised schema the same way <see cref="Accepted"/> does, rather than
    /// reflecting a C# type (e.g. <c>EditRequest</c>) a second time, so it holds for any future
    /// tool with a similar nested shape without naming that type anywhere in <c>Dispatch/</c>.
    /// Exactly one level deep, per D-06 — not a general recursive validator. Empty for a schema
    /// with no array-of-object properties.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ArrayItemProperties { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The subset of <see cref="Required"/> that is semantically required per
    /// <see cref="CosyRequiredAttribute"/> (D-02) -- set only by <see cref="SchemaOf"/>'s
    /// CosyRequiredMap union, never by <see cref="Project"/> itself. This exists because
    /// <see cref="Required"/> can ALSO contain names that are required only by the pre-existing
    /// schema-emitter defect (COSY-0004/COSY-0005: a C# parameter with no default value is
    /// schema-required regardless of nullability) for any tool not swept onto the D-01 shape.
    /// As of Phase 12.3 all 18 tools ARE swept, so no live schema emits a required[] and the two
    /// sets currently coincide. The distinction stays because it is what stops a NEWLY added tool
    /// -- or a parameter that loses its default -- from silently acquiring a null-as-absent rule
    /// it was never marked for. The case that forced it was compile_check's <c>project</c>, which
    /// was still schema-required when this guard landed in Plan 01 and was swept to
    /// <c>string? project = null</c> in Plan 02; it is deliberately NOT [CosyRequired] either way,
    /// because its handler resolves a null project to the default.
    /// <see cref="FirstMissing"/> treats an explicit JSON null as absent ONLY for names in this
    /// set. Measured live, full-suite run (2026-09-10): applying null-as-absent to the whole of
    /// <see cref="Required"/> broke 8 of 9 CompileCheckTests and others, because every one of them
    /// legitimately sends <c>project: null</c> to mean "use the default project" -- a call that
    /// worked before Task 2 (FirstMissing tested key presence only) and must keep working for
    /// every tool this plan does not touch. Empty by default, so a tool with no CosyRequired
    /// parameters (i.e. every tool except apply_edits_verified, in this plan) is completely
    /// unaffected by the null-as-absent rule -- the exact pre-existing behavior is preserved.
    /// </summary>
    public IReadOnlyList<string> SemanticallyRequired { get; init; } = Array.Empty<string>();

    /// <summary>Sentinel for a tool whose schema could not be read at all. Guards degrade to
    /// pass-through. Distinct from a tool that declares zero parameters (e.g. workspace_close),
    /// which IS known — see <see cref="IsDeclared"/>.</summary>
    public static readonly ToolArgumentSchema Unknown =
        new(Array.Empty<string>(), Array.Empty<string>(), IsDeclared: false);

    /// <summary>
    /// True iff this schema was actually read from a JSON object (<see cref="Project"/>'s
    /// non-object guard was not hit), regardless of whether either list came out empty. Phase
    /// 12.3: previously derived from <c>Accepted.Count &gt; 0</c>, which conflated "declared an
    /// empty vocabulary" (workspace_close — a real, answerable statement) with "could not be
    /// read at all" (the genuine unreadable-schema fallback) — the same sentinel stood for both,
    /// so a zero-parameter tool silently stood the guard down. This discriminator is set
    /// explicitly at every construction site rather than defaulted, so it cannot re-conflate.
    /// </summary>
    public bool IsKnown => IsDeclared;

    /// <summary>
    /// The first required parameter absent from <paramref name="args"/>, in schema order; null when
    /// every required parameter is present. First-only, not all: <c>InvalidArgumentDetails.param</c>
    /// is singular by ADR-0004 §4, and <see cref="Accepted"/> already carries the whole vocabulary
    /// the caller needs to correct a second mistake without another round trip.
    ///
    /// Phase 12.3 planner assumption 2: for a <see cref="CosyRequiredAttribute"/>-marked (i.e.
    /// <see cref="SemanticallyRequired"/>) parameter, a key present with value JSON null counts as
    /// absent, not present. Without this, `edits: null` binds, hits the handler's own
    /// `edits ??= Array.Empty&lt;EditRequest&gt;()` guard, and returns a silently-wrong zero-edit
    /// success — an explicitly null argument is materially absent. Scoped to
    /// <see cref="SemanticallyRequired"/> rather than all of <see cref="Required"/>: see that
    /// property's doc for why the wider scope broke other tools' legitimate `null` callers.
    /// </summary>
    public string? FirstMissing(IDictionary<string, JsonElement>? args)
    {
        foreach (var name in Required)
        {
            if (args is null || !args.TryGetValue(name, out var value))
                return name;
            if (value.ValueKind == JsonValueKind.Null && SemanticallyRequired.Contains(name, StringComparer.Ordinal))
                return name;
        }
        return null;
    }
}

/// <summary>
/// Dispatch-level repair for caller argument-shape faults (ADR-0019).
///
/// The SDK binds a tools/call arguments dictionary to the handler's parameters before the handler
/// runs. A missing required parameter therefore throws inside the binder, and the caller receives
/// the SDK's generic "An error occurred invoking '&lt;tool&gt;'." — no parameter name, no structure,
/// and identical across every tool. Cosy's whole ADR-0004 error contract is bypassed precisely
/// where an agent most needs it: it has just sent a name the tool does not have, and the reply
/// tells it nothing about which names the tool does have. Measured 2026-09-05: an agent read four
/// such replies as "the server is faulted" and abandoned the tool surface for hand-computed spans.
///
/// This guard runs the check the binder's throw would otherwise report opaquely, before dispatch,
/// and answers in the ordinary envelope. It is one filter rather than a null-check in each of the
/// eighteen tools deliberately.
///
/// Phase 12.3 (ADR-0023) made this guard the SOLE requiredness mechanism. Every tool parameter's
/// advertised <c>required[]</c> is now permanently empty, by design (D-01): a schema-level
/// rejection happens ABOVE Cosy, in the calling client's own validator, before a request is even
/// sent — the agent receives an opaque "failed tool call" with no contract and no actionable
/// content Cosy can ever narrate. Requiredness is instead signalled advisorily, in the one channel
/// an agent reliably reads (each parameter's description, prefixed by
/// <see cref="SchemaRequiredPrefix"/> from the <see cref="CosyRequiredAttribute"/> marker), and
/// enforced here, where a rejection always comes back through ADR-0004's own envelope.
/// </summary>
public static class ArgumentGuard
{
    /// <summary>
    /// The <c>ParamName</c> the SDK's argument binder (Microsoft.Extensions.AI's
    /// AIFunctionFactory) uses when the arguments dictionary itself is at fault. This is the
    /// discriminator — never the message text — that separates a caller's malformed arguments
    /// from a genuine internal ArgumentException thrown by Roslyn or by handler code, which names
    /// one of its own parameters instead. Matching on the structural property keeps an internal
    /// defect reported as a defect rather than relabelled as the caller's mistake.
    /// </summary>
    private const string BinderParamName = "arguments";

    /// <summary>
    /// Reproduces the null-omission the SDK's own serializer applies to a tool's returned object,
    /// so a guard-authored envelope is byte-shaped like every hand-authored one (no <c>data</c>
    /// key on an error). Deliberately not McpJsonUtilities.DefaultOptions: that resolver is
    /// source-generated over the SDK's own protocol types and has no contract to serialize
    /// Cosy's Envelope.
    /// </summary>
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Schemas are immutable for the process lifetime (tools are singletons registered at startup),
    // so the JsonElement walk below is paid once per tool rather than once per call.
    private static readonly ConcurrentDictionary<string, ToolArgumentSchema> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Project a tool's advertised input schema into its required/accepted name lists, then union
    /// in <see cref="CosyRequiredMap"/>'s semantic-requiredness list (D-02b) — the only remaining
    /// requiredness source once D-01 empties the schema's own <c>required[]</c> for every real
    /// tool. This is the seam where the union happens (not <see cref="Project"/> itself) because
    /// this is the only place that has the tool's name.
    /// </summary>
    public static ToolArgumentSchema SchemaOf(McpServerTool? tool)
    {
        if (tool is null) return ToolArgumentSchema.Unknown;
        return Cache.GetOrAdd(tool.ProtocolTool.Name, static (name, t) =>
        {
            var projected = Project(t.ProtocolTool.InputSchema);
            if (!projected.IsKnown) return projected;

            var cosyRequired = CosyRequiredMap.For(name);
            if (cosyRequired.Count == 0) return projected;

            var required = projected.Required.Union(cosyRequired, StringComparer.Ordinal).ToList();
            return projected with { Required = required, SemanticallyRequired = cosyRequired };
        }, tool);
    }

    /// <summary>
    /// Read <c>required[]</c> and <c>properties{}</c> out of a JSON Schema object. Deliberately
    /// left reading the schema's own <c>required[]</c> unchanged (not repointed to
    /// <see cref="CosyRequiredMap"/>): this method is public and six pre-existing
    /// ArgumentGuardTests facts call it directly with hand-built schemas that still populate
    /// <c>required[]</c>. After D-01 the real emitted schema's <c>required[]</c> is always empty,
    /// so for a live tool this union degenerates to exactly <see cref="CosyRequiredMap"/> — see
    /// <see cref="SchemaOf"/>, the seam that actually performs the union.
    /// </summary>
    public static ToolArgumentSchema Project(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return ToolArgumentSchema.Unknown;

        var required = new List<string>();
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in req.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is string s)
                    required.Add(s);
            }
        }

        var accepted = new List<string>();
        var arrayItemProperties = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
            {
                accepted.Add(p.Name);

                // D-06: one level of nested vocabulary for an array-typed property whose items
                // schema is itself an object. Read straight off the advertised schema -- the same
                // discipline Accepted above already follows -- so this stays immune to drift the
                // same way the rest of Project is, and holds for a second nested shape if one is
                // ever added without this file ever naming a concrete C# element type.
                //
                // "type" is checked with DeclaresJsonType rather than a plain string comparison
                // because Phase 12.3 D-01/D-13 made every parameter nullable: the live emitted
                // schema for a nullable array (EditRequest[]?) writes "type":["array","null"], an
                // array of strings, not the bare string "array" a non-nullable parameter would
                // carry. Measured live against apply_edits_verified's own emitted schema -- a
                // plain string-equality check silently never matched, and the whole nested check
                // degraded to a no-op for the one tool this task exists to cover.
                if (p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("type", out var typeEl)
                    && DeclaresJsonType(typeEl, "array")
                    && p.Value.TryGetProperty("items", out var itemsEl)
                    && itemsEl.ValueKind == JsonValueKind.Object
                    && itemsEl.TryGetProperty("properties", out var itemPropsEl)
                    && itemPropsEl.ValueKind == JsonValueKind.Object)
                {
                    var itemKeys = new List<string>();
                    foreach (var ip in itemPropsEl.EnumerateObject())
                        itemKeys.Add(ip.Name);
                    arrayItemProperties[p.Name] = itemKeys;
                }
            }
        }

        // A schema actually read always projects to a DECLARED schema, even when both lists come
        // out empty (workspace_close: zero declared parameters is a real, answerable vocabulary,
        // not "could not be read"). The non-object guard above, returning Unknown, is the only
        // unreadable-schema fallback and is unchanged — deliberately, per two pinned facts:
        // Project_OnANonObjectSchema_YieldsUnknown_SoTheGuardStandsDown and
        // UnknownSchema_NeitherShortCircuitsNorConverts.
        return new ToolArgumentSchema(required, accepted, IsDeclared: true)
        {
            ArrayItemProperties = arrayItemProperties,
        };
    }

    /// <summary>
    /// True when a JSON Schema "type" node declares <paramref name="wanted"/>, in either shape the
    /// SDK's schema emitter produces: a bare string (<c>"type":"array"</c>) for a non-nullable
    /// parameter, or an array of strings (<c>"type":["array","null"]</c>) for a nullable one.
    /// Phase 12.3 made every tool parameter nullable, so the second shape is now the live one.
    /// </summary>
    private static bool DeclaresJsonType(JsonElement typeEl, string wanted)
    {
        if (typeEl.ValueKind == JsonValueKind.String)
            return typeEl.GetString() == wanted;

        if (typeEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in typeEl.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String && e.GetString() == wanted)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pre-check <paramref name="args"/> against <paramref name="schema"/>, then invoke
    /// <paramref name="next"/>, converting a binder-level argument fault into the ordinary
    /// invalid_argument envelope. Any other outcome — success, a tool-authored error envelope, or
    /// an exception that is not the binder's — passes through untouched.
    /// </summary>
    public static async ValueTask<CallToolResult> InvokeAsync(
        ToolArgumentSchema schema,
        IDictionary<string, JsonElement>? args,
        Func<ValueTask<CallToolResult>> next)
    {
        // D-04/D-05: unknown-key check runs first, ahead of the missing-required check below —
        // pinned by UnknownKey_IsReportedBeforeAMissingRequiredArgument (Task 3). A call carrying
        // a wrong name usually ALSO looks like a missing required parameter, and naming the wrong
        // key is the more actionable of the two reports. Gated on IsKnown, matching the existing
        // degrade-to-pass-through contract: once IsKnown means "declared" rather than
        // "Accepted.Count > 0", a tool with an empty vocabulary (workspace_close) needs no special
        // case here — every key it sees is simply unaccepted, and schema.Accepted is correctly the
        // empty list the rejection payload carries.
        if (schema.IsKnown && args is not null)
        {
            foreach (var key in args.Keys)
            {
                if (!schema.Accepted.Contains(key, StringComparer.Ordinal))
                    return Fail(ToolError.UnknownArgument(key, schema.Accepted));
            }

            // D-06: exactly one level of recursion, into array-typed properties whose items
            // schema is itself an object (ArrayItemProperties, built in Project). This is an
            // extension of the same boundary check above -- not a second filter -- so it lives at
            // the dispatch boundary once, per D-05, and holds for a tool that does not exist yet.
            // A property present but not a JSON array, or an element that is not a JSON object, is
            // a type mismatch left for the binder to report via the existing IsBinderFault arm
            // below -- this check only ever reports a KEY it did not expect, never a shape it did.
            foreach (var (topLevelKey, itemAccepted) in schema.ArrayItemProperties)
            {
                if (!args.TryGetValue(topLevelKey, out var arrayValue) || arrayValue.ValueKind != JsonValueKind.Array)
                    continue;

                var index = 0;
                foreach (var element in arrayValue.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var elementProp in element.EnumerateObject())
                        {
                            if (!itemAccepted.Contains(elementProp.Name, StringComparer.Ordinal))
                                return Fail(ToolError.UnknownArgument(
                                    $"{topLevelKey}[{index}].{elementProp.Name}", itemAccepted));
                        }
                    }
                    index++;
                }
            }
        }

        if (schema.IsKnown && schema.FirstMissing(args) is string missing)
            return Fail(ToolError.MissingRequiredArgument(missing, schema.Accepted));

        try
        {
            return await next();
        }
        catch (Exception ex) when (schema.IsKnown && IsBinderFault(ex))
        {
            // Reached when every required argument is present but one of them cannot be bound: a
            // value of the wrong JSON type for its declared parameter. The binder's own message
            // names the offending parameter and is safe to RETURN -- it describes arguments the
            // caller itself just sent. It is still not WRITTEN to the trace file; that asymmetry
            // is deliberate and is explained at TraceSink.EmitFault.
            return Fail(ToolError.ArgumentBindingFailed(ex.Message, schema.Accepted));
        }
    }

    /// <summary>
    /// True for the two exception shapes the SDK's argument binder raises, and only those:
    /// <list type="bullet">
    ///   <item>ArgumentException whose ParamName is <see cref="BinderParamName"/> -- a required
    ///   parameter is absent from the dictionary.</item>
    ///   <item>JsonException -- a supplied argument cannot be converted to its declared type
    ///   (measured 2026-09-05: <c>max:"abc"</c> against <c>int?</c> throws exactly this).</item>
    /// </list>
    /// Both are matched structurally, never by message text.
    ///
    /// The JsonException arm rests on an invariant worth stating outright, because it is what
    /// keeps a genuine internal defect from being relabelled as the caller's mistake: no Cosy
    /// tool body deserializes caller-supplied JSON -- there is no JsonSerializer.Deserialize,
    /// JsonDocument.Parse or JsonNode.Parse anywhere under Tools/, Search/, Symbols/, Refactor/,
    /// Workspace/ or Source/ -- so a JsonException reaching this frame cannot have come from a
    /// handler. TraceSink does parse JSON, but it wraps this guard rather than running inside it.
    /// If a tool ever starts parsing caller JSON, this arm must narrow or that tool's internal
    /// faults will be reported as invalid_argument.
    /// </summary>
    private static bool IsBinderFault(Exception ex)
        => ex is JsonException
           || (ex is ArgumentException argEx && argEx.ParamName == BinderParamName);

    private static CallToolResult Fail(ToolError error)
    {
        var envelope = Envelope<object>.Err(error);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, EnvelopeJson) }],
        };
    }
}
