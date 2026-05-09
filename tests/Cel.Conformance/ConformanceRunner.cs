using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cel.Conformance.TextProto;
using Cel.Diagnostics;
using Cel.Extensions;
using Cel.Runtime;
using Cel.Values;
using Google.Protobuf.Reflection;

namespace Cel.Conformance;

public enum TestOutcome
{
    Pass,
    Fail,
    Skip,
}

public sealed record TestResult(
    string File,
    string Section,
    string Name,
    TestOutcome Outcome,
    string? Detail = null);

public sealed record FileResult(
    string FileName,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestResult> FailureSamples);

public static class ConformanceRunner
{
    /// <summary>
    /// Type provider built once at startup, populated with every <see cref="MessageDescriptor"/>
    /// reachable from the generated cel-spec test message types (including nested types and
    /// well-known wrappers). Shared across every test run.
    /// </summary>
    private static readonly ProtoTypeProvider Provider = BuildProvider();

    private static ProtoTypeProvider BuildProvider()
    {
        var descriptors = new List<MessageDescriptor>();
        Walk(global::Cel.Expr.Conformance.Proto3.TestAllTypes.Descriptor, descriptors);
        Walk(global::Cel.Expr.Conformance.Proto3.NestedTestAllTypes.Descriptor, descriptors);
        Walk(global::Cel.Expr.Conformance.Proto2.TestAllTypes.Descriptor, descriptors);
        Walk(global::Cel.Expr.Conformance.Proto2.NestedTestAllTypes.Descriptor, descriptors);
        return new ProtoTypeProvider(descriptors);

        static void Walk(MessageDescriptor d, List<MessageDescriptor> sink)
        {
            sink.Add(d);
            foreach (var nested in d.NestedTypes)
            {
                Walk(nested, sink);
            }
        }
    }

    /// <summary>
    /// Run conformance tests from <paramref name="testdataDir"/>. Each <c>.textproto</c> file
    /// is treated as a <c>SimpleTestFile</c>; tests using features this implementation does
    /// not yet support (proto messages, function decls, disable_check, typed_result, etc.) are
    /// reported as <see cref="TestOutcome.Skip"/>.
    /// </summary>
    public static IReadOnlyList<FileResult> Run(string testdataDir, IReadOnlyList<string>? onlyFiles = null)
    {
        var results = new List<FileResult>();
        var files = Directory.EnumerateFiles(testdataDir, "*.textproto").OrderBy(static p => p, StringComparer.Ordinal);
        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (onlyFiles is not null && !onlyFiles.Contains(name))
            {
                continue;
            }
            results.Add(RunFile(path));
        }
        return results;
    }

    public static FileResult RunFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        // Read as raw bytes mapped to Latin-1 chars (one byte → one char). This preserves the
        // byte fidelity that proto text format requires: escapes like \xff stay as a single byte
        // 0xFF, and literal non-ASCII chars in the source stay as their UTF-8 byte sequence
        // (rather than being collapsed to a single .NET char by UTF-8 decoding).
        var bytes = File.ReadAllBytes(path);
        var source = System.Text.Encoding.Latin1.GetString(bytes);
        TextProtoMessage tree;
        try
        {
            tree = TextProtoParser.Parse(source);
        }
        catch (FormatException ex)
        {
            return new FileResult(name, 0, 0, 0, 0, [new TestResult(name, "", "", TestOutcome.Fail, $"parse error: {ex.Message}")]);
        }

        var sections = tree.SubAll("section").ToList();
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<TestResult>();
        foreach (var section in sections)
        {
            var sectionName = section.Str("name") ?? "<unnamed>";
            foreach (var test in section.SubAll("test"))
            {
                var result = RunTest(name, sectionName, test);
                switch (result.Outcome)
                {
                    case TestOutcome.Pass: passed++; break;
                    case TestOutcome.Fail:
                        failed++;
                        if (failures.Count < 5) { failures.Add(result); }
                        break;
                    case TestOutcome.Skip: skipped++; break;
                }
            }
        }
        return new FileResult(name, passed + failed + skipped, passed, failed, skipped, failures);
    }

    private static TestResult RunTest(string fileName, string sectionName, TextProtoMessage test)
    {
        var name = test.Str("name") ?? "<unnamed>";
        if (test.Bool("disable_check") == true)
        {
            return new(fileName, sectionName, name, TestOutcome.Skip, "disable_check");
        }
        if (test.Bool("disable_macros") == true)
        {
            return new(fileName, sectionName, name, TestOutcome.Skip, "disable_macros");
        }
        if (test.Bool("check_only") == true)
        {
            return new(fileName, sectionName, name, TestOutcome.Skip, "check_only");
        }
        if (test.Sub("typed_result") is not null
            || test.Sub("any_eval_errors") is not null
            || test.Sub("any_unknowns") is not null
            || test.Sub("unknown") is not null)
        {
            return new(fileName, sectionName, name, TestOutcome.Skip, "unsupported matcher");
        }
        var expr = test.Str("expr");
        if (expr is null)
        {
            return new(fileName, sectionName, name, TestOutcome.Skip, "no expr");
        }

        // Build env from type_env + container.
        var envBuilder = CelEnv.NewBuilder()
            .UseTypeProvider(Provider)
            .Use(StringsExtension.Instance)
            .Use(MathExtension.Instance)
            .Use(EncodersExtension.Instance)
            .Use(SetsExtension.Instance);
        if (test.Str("container") is { } container)
        {
            envBuilder.SetContainer(container);
        }
        foreach (var decl in test.SubAll("type_env"))
        {
            if (ValueMapper.IsFunctionDecl(decl))
            {
                return new(fileName, sectionName, name, TestOutcome.Skip, "function type_env not supported");
            }
            var v = ValueMapper.ParseVariableDecl(decl);
            if (v is not null)
            {
                envBuilder.Variable(v);
            }
        }
        var env = envBuilder.Build();

        // Build bindings.
        var bindings = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var b in test.SubAll("bindings"))
        {
            var k = b.Str("key");
            var vMsg = b.Sub("value");
            if (k is null || vMsg is null) { continue; }
            var (cv, isUnknown, _) = ValueMapper.ParseExprValue(vMsg, Provider);
            if (isUnknown)
            {
                return new(fileName, sectionName, name, TestOutcome.Skip, "unknown bindings");
            }
            bindings[k] = cv;
        }

        // Compile + eval.
        CompiledProgram program;
        try
        {
            program = CelExpression.Compile(expr, env);
        }
        catch (CelCompileException ex)
        {
            // If the test expects an error, a compile-time failure counts as such.
            if (test.Sub("eval_error") is not null)
            {
                return new(fileName, sectionName, name, TestOutcome.Pass);
            }
            return new(fileName, sectionName, name, TestOutcome.Fail, $"compile: {ex.Message}");
        }

        CelValue actual;
        try
        {
            actual = program.EvaluateRaw(new MapActivation(bindings));
        }
        catch (Exception ex)
        {
            actual = CelValue.Error(ex.Message);
        }

        // Check expected.
        if (test.Sub("eval_error") is not null)
        {
            return actual is ErrorValue
                ? new(fileName, sectionName, name, TestOutcome.Pass)
                : new(fileName, sectionName, name, TestOutcome.Fail, $"expected error, got {Render(actual)}");
        }

        // Default expected when no matcher: bool true.
        var expectedValue = test.Sub("value") is { } valueMsg
            ? ValueMapper.ParseValue(valueMsg, Provider)
            : CelValue.True;

        if (CelEquality.Equals(actual, expectedValue))
        {
            return new(fileName, sectionName, name, TestOutcome.Pass);
        }
        return new(
            fileName, sectionName, name, TestOutcome.Fail,
            $"expected {Render(expectedValue)}, got {Render(actual)}");
    }

    private static string Render(CelValue v) => v switch
    {
        ErrorValue err => $"<error: {err.Message}>",
        ListValue l => "[" + string.Join(", ", l.Elements.Select(Render)) + "]",
        MapValue m => "{" + string.Join(", ", m.Entries.Select(kv => $"{Render(kv.Key)}: {Render(kv.Value)}")) + "}",
        StringValue s => $"\"{s.Value}\"",
        BytesValue b => "0x" + Convert.ToHexString(b.Value.AsSpan()),
        DurationValue d => d.Value.ToString(),
        TimestampValue t => t.Value.ToString(),
        TypeValue t => $"type({t.Inner.Name})",
        NullValue => "null",
        _ => v.ToClrObject()?.ToString() ?? "null",
    };
}
