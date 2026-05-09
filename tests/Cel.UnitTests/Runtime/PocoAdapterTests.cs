using System.Collections.Generic;
using System.Text.Json.Serialization;
using Cel.Diagnostics;
using Cel.Runtime;
using Cel.Types;

namespace Cel.UnitTests.Runtime;

public sealed class PocoAdapterTests
{
    private sealed class Plain
    {
        public string UserName { get; init; } = "";
        public int Age { get; init; }
    }

    private sealed class Annotated
    {
        [JsonPropertyName("user_name")]
        public string UserName { get; init; } = "";

        [JsonPropertyName("user_age")]
        public int Age { get; init; }

        [JsonIgnore]
        public string Secret { get; init; } = "";
    }

    private sealed class HttpRequest
    {
        public string HTTPMethod { get; init; } = "";
        public string Path { get; init; } = "";
    }

    private static object? Eval(string source, object root, PocoNamingConvention convention = PocoNamingConvention.PascalCase)
    {
        var env = CelEnv.NewBuilder()
            .UsePocoNaming(convention)
            .Variable("obj", CelTypes.Object("obj"))
            .Build();
        var bindings = new Dictionary<string, object?> { ["obj"] = root };
        return CelExpression.Compile(source, env)
            .Eval((IReadOnlyDictionary<string, object?>)bindings);
    }

    // ── PascalCase (default) ──

    [Fact]
    public void Default_ExactMatch_FindsClrName()
    {
        Assert.Equal("alice", Eval("obj.UserName", new Plain { UserName = "alice" }));
        Assert.Equal(30L, Eval("obj.Age", new Plain { UserName = "x", Age = 30 }));
    }

    [Fact]
    public void Default_SnakeFallback_StillWorks()
    {
        // Backward-compat: CEL `user_name` finds CLR `UserName`.
        Assert.Equal("alice", Eval("obj.user_name", new Plain { UserName = "alice" }));
    }

    // ── JsonPropertyName ──

    [Fact]
    public void JsonPropertyName_Overrides_ClrName()
    {
        var root = new Annotated { UserName = "alice", Age = 30, Secret = "shh" };
        Assert.Equal("alice", Eval("obj.user_name", root));
        Assert.Equal(30L, Eval("obj.user_age", root));
    }

    [Fact]
    public void JsonPropertyName_Hides_ClrName()
    {
        // After [JsonPropertyName("user_name")], the original `UserName` is no longer exposed —
        // matching System.Text.Json's behaviour. CEL access on the CLR name now errors.
        var root = new Annotated { UserName = "alice" };
        Assert.Throws<CelEvaluationException>(() => Eval("obj.UserName", root));
    }

    [Fact]
    public void JsonIgnore_HidesMember()
    {
        var root = new Annotated { Secret = "shh" };
        Assert.Throws<CelEvaluationException>(() => Eval("obj.Secret", root));
        Assert.Throws<CelEvaluationException>(() => Eval("obj.secret", root));
    }

    // ── CamelCase ──

    [Fact]
    public void CamelCase_TransformsClrNames()
    {
        var root = new Plain { UserName = "alice", Age = 30 };
        Assert.Equal("alice", Eval("obj.userName", root, PocoNamingConvention.CamelCase));
        Assert.Equal(30L, Eval("obj.age", root, PocoNamingConvention.CamelCase));
    }

    [Fact]
    public void CamelCase_PreservesAcronymRun()
    {
        // "HTTPMethod" → "httpMethod": leading run of capitals lowercased except the last,
        // which heads the next word.
        var root = new HttpRequest { HTTPMethod = "GET", Path = "/" };
        Assert.Equal("GET", Eval("obj.httpMethod", root, PocoNamingConvention.CamelCase));
    }

    [Fact]
    public void CamelCase_OriginalNameNotExposed()
    {
        var root = new Plain { UserName = "alice" };
        Assert.Throws<CelEvaluationException>(() =>
            Eval("obj.UserName", root, PocoNamingConvention.CamelCase));
    }

    // ── SnakeCase ──

    [Fact]
    public void SnakeCase_TransformsClrNames()
    {
        var root = new Plain { UserName = "alice", Age = 30 };
        Assert.Equal("alice", Eval("obj.user_name", root, PocoNamingConvention.SnakeCase));
        Assert.Equal(30L, Eval("obj.age", root, PocoNamingConvention.SnakeCase));
    }

    [Fact]
    public void SnakeCase_AcronymBoundary()
    {
        var root = new HttpRequest { HTTPMethod = "GET", Path = "/" };
        Assert.Equal("GET", Eval("obj.http_method", root, PocoNamingConvention.SnakeCase));
    }

    // ── ScreamingSnakeCase ──

    [Fact]
    public void ScreamingSnakeCase_Transforms()
    {
        var root = new Plain { UserName = "alice", Age = 30 };
        Assert.Equal("alice", Eval("obj.USER_NAME", root, PocoNamingConvention.ScreamingSnakeCase));
    }

    // ── KebabCase ──

    [Fact]
    public void KebabCase_TransformsThroughDirectAdapter()
    {
        // CEL identifiers can't contain hyphens; verify the adapter exposes kebab-case names
        // directly. Programs would need `dyn`/index access to reach them in CEL syntax.
        var adapter = new PocoAdapter(PocoNamingConvention.KebabCase);
        var ok = adapter.TryGet(new Plain { UserName = "alice" }, "user-name", out var v);
        Assert.True(ok);
        Assert.Equal("alice", v);
    }

    // ── Direct PocoAdapter API ──

    [Fact]
    public void PocoAdapter_DirectApi()
    {
        var adapter = new PocoAdapter(PocoNamingConvention.SnakeCase);
        var ok = adapter.TryGet(new Plain { UserName = "alice", Age = 30 }, "user_name", out var v);
        Assert.True(ok);
        Assert.Equal("alice", v);
    }

    [Fact]
    public void PocoAdapter_HasField()
    {
        var adapter = new PocoAdapter(PocoNamingConvention.CamelCase);
        var inst = new Plain { UserName = "alice" };
        Assert.True(adapter.HasField(inst, "userName"));
        Assert.False(adapter.HasField(inst, "UserName"));
        Assert.False(adapter.HasField(inst, "missing"));
    }
}
