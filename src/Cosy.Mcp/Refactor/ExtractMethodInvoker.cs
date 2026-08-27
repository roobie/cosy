using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;

namespace Cosy.Mcp.Refactor;

/// <summary>
/// Isolated Roslyn refactoring harness for the ExtractMethodCodeRefactoringProvider
/// (Phase 10 Plan 02). The class hides reflection / MEF / CodeAction title-filter
/// mechanics behind a single <see cref="InvokeAsync"/> entry, returning a
/// discriminated <see cref="ExtractInvocationOutcome"/>. The wire-shaped tool layer
/// (ExtractMethodTool) consumes this output and translates it to the MCP envelope.
///
/// Reference implementation: tests/Cosy.Mcp.Tests/Refactor/SnapshotCompositionProbeTests.cs
/// (Wave-0 deliverable; same reflection mechanics).
///
/// Empirical discovery from 10-01: the provider type lives in
/// <c>Microsoft.CodeAnalysis.Features</c> (language-agnostic), NOT in
/// <c>Microsoft.CodeAnalysis.CSharp.Features</c> as RESEARCH §Pattern 1 originally
/// stated. The C# Features DLL is still required at runtime because the language
/// service (<c>CSharpExtractMethodService</c>) is what the provider delegates to via
/// the workspace's MEF host — both DLLs must ship to bin/ (handled by csproj's
/// PackageReference without ExcludeAssets).
/// </summary>
internal static class ExtractMethodInvoker
{
    private const string ProviderTypeFullName =
        "Microsoft.CodeAnalysis.CodeRefactorings.ExtractMethod.ExtractMethodCodeRefactoringProvider";
    private const string ExtractMethodTitlePrefix = "Extract method";
    private const string ExtractLocalFunctionTitlePrefix = "Extract local function";

    // Lazy-construct the provider once per process. Reflection-load both assemblies:
    //   - Microsoft.CodeAnalysis.Features holds the provider type itself.
    //   - Microsoft.CodeAnalysis.CSharp.Features holds CSharpExtractMethodService,
    //     which the provider resolves via the workspace's MEF host at
    //     ComputeRefactoringsAsync time. Forcing it into the AppDomain guarantees
    //     MefHostServices.LoadNearbyAssemblies enumerates it.
    //
    // If construction throws (would indicate a Roslyn version bump that changed the
    // type shape), the exception escapes to the tool layer, which catches it as
    // ToolError.Internal (RenameTool.cs:296-300 precedent).
    private static readonly Lazy<CodeRefactoringProvider> s_provider = new(() =>
    {
        var featuresAsm = Assembly.Load("Microsoft.CodeAnalysis.Features");
        var providerType = featuresAsm.GetType(ProviderTypeFullName, throwOnError: true)!;
        _ = Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");
        // The provider's primary constructor is parameterless even though it carries
        // [ImportingConstructor]; Activator with nonPublic: true is sufficient
        // (10-01 verified empirically).
        var instance = Activator.CreateInstance(providerType, nonPublic: true)
            ?? throw new InvalidOperationException(
                $"Activator.CreateInstance returned null for {ProviderTypeFullName}");
        return (CodeRefactoringProvider)instance;
    });

    /// <summary>
    /// Invoke the extract-method refactoring on the given document/span. Returns
    /// <see cref="ExtractInvocationOutcome.Success"/> with the changed solution when
    /// Roslyn produced an "Extract method" CodeAction, or
    /// <see cref="ExtractInvocationOutcome.RoslynRefused"/> with a reason when the
    /// provider declined (no matching action, no ApplyChangesOperation).
    /// </summary>
    public static async Task<ExtractInvocationOutcome> InvokeAsync(
        Document document,
        TextSpan textSpan,
        CancellationToken ct)
    {
        var provider = s_provider.Value;

        var collected = new List<CodeAction>();
        var context = new CodeRefactoringContext(document, textSpan, action => collected.Add(action), ct);
        await provider.ComputeRefactoringsAsync(context);

        // Title filter (Pitfall 4 — defense in depth). "Extract method" matches both
        // method and local-function refactors prefix-only; explicitly exclude the
        // local-function variant. StringComparison.Ordinal because Roslyn emits
        // English literals from FeaturesResources.
        var extractMethodActions = collected
            .Where(a => a.Title.StartsWith(ExtractMethodTitlePrefix, StringComparison.Ordinal)
                     && !a.Title.StartsWith(ExtractLocalFunctionTitlePrefix, StringComparison.Ordinal))
            .ToList();

        if (extractMethodActions.Count == 0)
        {
            // D-08: Roslyn refused → tool layer maps to unsupported_option.
            return new ExtractInvocationOutcome.RoslynRefused("no_extract_method_action");
        }

        var action = extractMethodActions[0];
        var ops = await action.GetOperationsAsync(ct);
        var apply = ops.OfType<ApplyChangesOperation>().FirstOrDefault();
        if (apply is null)
        {
            // Defensive — every successful CodeAction in practice carries an
            // ApplyChangesOperation, but this guards against a future Roslyn
            // change that introduces non-applying CodeActions for extract.
            return new ExtractInvocationOutcome.RoslynRefused("no_apply_changes_operation");
        }

        return new ExtractInvocationOutcome.Success(apply.ChangedSolution, action.Title);
    }

    /// <summary>
    /// Filename + first-line heuristic for generated code. Tool layer rejects
    /// generated files with unsupported_option BEFORE invoking the provider
    /// (RESEARCH Pitfall 2, threat T-10-02-06). Filename suffixes match the
    /// most common Roslyn / designer / T4 conventions; the first-line check
    /// catches the `// <auto-generated` marker emitted by many code generators.
    /// </summary>
    internal static bool IsGeneratedCode(Document doc, SourceText text)
    {
        var path = doc.FilePath;
        if (path is not null)
        {
            // Lowercase ordinal compare — filename conventions are case-insensitive on Windows.
            if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // First non-whitespace line check: `// <auto-generated` (with or without trailing space/>).
        // Bounded by length 4096 to avoid scanning the entire file for a marker that, by
        // convention, lives at the top.
        var headSpan = text.Length > 4096 ? text.GetSubText(new TextSpan(0, 4096)) : text;
        var head = headSpan.ToString();
        foreach (var rawLine in head.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0) continue;
            return line.StartsWith("// <auto-generated", StringComparison.Ordinal);
        }
        return false;
    }
}

/// <summary>
/// Discriminated outcome of <see cref="ExtractMethodInvoker.InvokeAsync"/>.
/// Co-located with the invoker to keep all refactor-internal types out of
/// Cosy.Mcp.Contracts (per plan task 1 — surgical-change discipline).
/// </summary>
internal abstract record ExtractInvocationOutcome
{
    public sealed record Success(Solution ChangedSolution, string ActionTitle) : ExtractInvocationOutcome;
    public sealed record RoslynRefused(string Reason) : ExtractInvocationOutcome;
}
