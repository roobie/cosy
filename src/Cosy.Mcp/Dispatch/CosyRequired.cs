using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Dispatch;

/// <summary>
/// Marks a tool parameter as semantically required (D-02): its absence is a caller error, not
/// "use the default". This encodes INTENT, never "has no C# default value today" — a parameter the
/// handler already defaults for absence (e.g. apply_edits_verified.verify) must NOT be marked, or
/// marking it would move a schema-only defect into ArgumentGuard rather than removing it (see
/// 12.3-RESEARCH.md "Semantic requiredness"). The single source of truth feeds three consumers:
/// the tools/list description transform (<see cref="SchemaRequiredPrefix"/>), ArgumentGuard's
/// repointed requiredness source, and the drift guard's assertion that required[] stays empty.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class CosyRequiredAttribute : Attribute
{
}

/// <summary>
/// Reflection-built map from a tool's registered name to the wire names of its
/// <see cref="CosyRequiredAttribute"/>-marked parameters. Built once — attributes are fixed at
/// compile time, the same justification <c>ArgumentGuard.Cache</c> already uses for schema
/// projections (tools are singletons registered at startup).
/// </summary>
public static class CosyRequiredMap
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Map = Build();

    /// <summary>The wire names of <paramref name="toolName"/>'s [CosyRequired] parameters; an
    /// empty list for an unknown tool name or a tool with no marked parameters.</summary>
    public static IReadOnlyList<string> For(string toolName)
        => Map.TryGetValue(toolName, out var names) ? names : Array.Empty<string>();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Build()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        // Walk shape copied verbatim from TraceArgShapingTests.Reflection_EveryStringToolParameter_
        // IsClassified (12.3-PATTERNS.md) — the same [McpServerToolType]/[McpServerTool] surface
        // walk, already proven enumerable and stable at test time.
        foreach (var type in typeof(CosyRequiredAttribute).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr?.Name is not string toolName) continue;

                var required = new List<string>();
                foreach (var param in method.GetParameters())
                {
                    // The SDK binds request parameters by C# identifier verbatim — no
                    // snake_case mapping the way response fields carry (ADR-0004 §2 governs
                    // responses only). param.Name IS the wire key.
                    if (param.GetCustomAttribute<CosyRequiredAttribute>() is not null)
                        required.Add(param.Name!);
                }

                if (required.Count > 0)
                    map[toolName] = required;
            }
        }

        return map;
    }
}

/// <summary>
/// D-02(a): prefixes the description of each <see cref="CosyRequiredAttribute"/>-marked
/// parameter in an emitted tools/list schema with a REQUIRED. marker — the one channel an agent
/// reliably reads, since the schema's own required[] array is permanently empty (D-01).
/// </summary>
public static class SchemaRequiredPrefix
{
    private const string Marker = "REQUIRED. ";

    /// <summary>
    /// Returns a NEW JsonElement with each named parameter's description prefixed; does not
    /// mutate <paramref name="schema"/>, which is an immutable view over a buffer the SDK owns.
    /// Idempotent: a description already carrying the marker is left alone, so a second filter
    /// pass cannot double-prefix.
    /// </summary>
    public static JsonElement Apply(JsonElement schema, IReadOnlyList<string> requiredParams)
    {
        // Pitfall 1 (12.3-RESEARCH.md): JsonElement's VALUE is immutable even though
        // Tool.InputSchema's PROPERTY is mutable — round-trip through JsonNode to edit a nested
        // description string.
        var node = JsonNode.Parse(schema.GetRawText())
            ?? throw new InvalidOperationException("schema parsed to a null JsonNode");

        if (node.AsObject()["properties"] is JsonObject properties)
        {
            foreach (var name in requiredParams)
            {
                if (properties[name] is not JsonObject propertySchema) continue;

                var existing = propertySchema["description"]?.GetValue<string>() ?? string.Empty;
                if (existing.StartsWith(Marker, StringComparison.Ordinal)) continue;

                propertySchema["description"] = Marker + existing;
            }
        }

        // The clone is mandatory: this intermediate JsonDocument is disposed (the `using`) before
        // the caller ever reads the returned element, so the returned element must own its own
        // buffer rather than pointing into a disposed one.
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
