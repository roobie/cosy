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
    IReadOnlyList<string> Accepted)
{
    /// <summary>Sentinel for a tool whose schema could not be read. Guards degrade to pass-through.</summary>
    public static readonly ToolArgumentSchema Unknown =
        new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>False for <see cref="Unknown"/> — a schema with no properties at all names nothing
    /// useful, so the guard must not claim to know what the tool accepts.</summary>
    public bool IsKnown => Accepted.Count > 0;

    /// <summary>
    /// The first required parameter absent from <paramref name="args"/>, in schema order; null when
    /// every required parameter is present. First-only, not all: <c>InvalidArgumentDetails.param</c>
    /// is singular by ADR-0004 §4, and <see cref="Accepted"/> already carries the whole vocabulary
    /// the caller needs to correct a second mistake without another round trip.
    /// </summary>
    public string? FirstMissing(IDictionary<string, JsonElement>? args)
    {
        foreach (var name in Required)
        {
            if (args is null || !args.ContainsKey(name))
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
/// eighteen tools deliberately — a per-tool check would require making every required parameter
/// optional in C#, which would strip all 24 of them from the advertised <c>required</c> arrays and
/// destroy the very schema signal a validating client uses to catch this locally.
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

    /// <summary>Project a tool's advertised input schema into its required/accepted name lists.</summary>
    public static ToolArgumentSchema SchemaOf(McpServerTool? tool)
    {
        if (tool is null) return ToolArgumentSchema.Unknown;
        return Cache.GetOrAdd(tool.ProtocolTool.Name, static (_, t) => Project(t.ProtocolTool.InputSchema), tool);
    }

    /// <summary>Read <c>required[]</c> and <c>properties{}</c> out of a JSON Schema object.</summary>
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
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
                accepted.Add(p.Name);
        }

        return accepted.Count == 0 && required.Count == 0
            ? ToolArgumentSchema.Unknown
            : new ToolArgumentSchema(required, accepted);
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
