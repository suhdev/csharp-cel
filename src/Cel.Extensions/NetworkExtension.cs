using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Cel.Types;
using Cel.Values;

namespace Cel.Extensions;

/// <summary>
/// Port of cel-go's <c>ext/network</c> library: <c>ip</c>, <c>isIP</c>, <c>cidr</c>, plus
/// instance methods for predicates (<c>isUnspecified</c>, <c>isLoopback</c>, etc.) and CIDR
/// containment checks. IP addresses canonicalise IPv4-in-IPv6 (<c>::ffff:c0a8:1</c>) for
/// equality so they compare equal to the bare IPv4 form.
/// </summary>
public sealed class NetworkExtension : ICelExtension
{
    public static readonly NetworkExtension Instance = new();
    private NetworkExtension() { }

    private const string IpType = "net.IP";
    private const string CidrType = "net.CIDR";

    public void ConfigureEnv(CelEnv.Builder b)
    {
        var ipT = CelTypes.Object(IpType);
        var cidrT = CelTypes.Object(CidrType);

        // Type denotations as named variables of type `type`. The evaluator pre-populates
        // these in the activation when the conformance harness asks for the constants.
        b.Variable(IpType, CelTypes.Type);
        b.Variable(CidrType, CelTypes.Type);

        b.Function("ip",
            new OverloadDecl("string_to_ip", [CelTypes.String], ipT));
        b.Function("cidr",
            new OverloadDecl("string_to_cidr", [CelTypes.String], cidrT));
        // Only string is accepted by isIP — passing a CIDR is a compile-time error per the
        // cel-go semantics (the conformance corpus has explicit `is_ip_cidr_compile_error`).
        b.Function("isIP",
            new OverloadDecl("is_ip_string", [CelTypes.String], CelTypes.Bool));

        b.Function("ip.isCanonical",
            new OverloadDecl("ip_is_canonical_string", [CelTypes.String], CelTypes.Bool));

        // string(ip) / string(cidr): augment the existing `string` function from Stdlib.
        b.Function("string",
            new OverloadDecl("ip_to_string", [ipT], CelTypes.String),
            new OverloadDecl("cidr_to_string", [cidrT], CelTypes.String));

        // Instance methods on net.IP.
        b.Function("family",
            new OverloadDecl("ip_family", [ipT], CelTypes.Int, IsInstance: true));
        b.Function("isUnspecified",
            new OverloadDecl("ip_is_unspecified", [ipT], CelTypes.Bool, IsInstance: true));
        b.Function("isLoopback",
            new OverloadDecl("ip_is_loopback", [ipT], CelTypes.Bool, IsInstance: true));
        b.Function("isGlobalUnicast",
            new OverloadDecl("ip_is_global_unicast", [ipT], CelTypes.Bool, IsInstance: true));
        b.Function("isLinkLocalMulticast",
            new OverloadDecl("ip_is_link_local_multicast", [ipT], CelTypes.Bool, IsInstance: true));
        b.Function("isLinkLocalUnicast",
            new OverloadDecl("ip_is_link_local_unicast", [ipT], CelTypes.Bool, IsInstance: true));

        // Instance methods on net.CIDR.
        b.Function("containsIP",
            new OverloadDecl("cidr_contains_ip_string", [cidrT, CelTypes.String], CelTypes.Bool, IsInstance: true),
            new OverloadDecl("cidr_contains_ip_ip", [cidrT, ipT], CelTypes.Bool, IsInstance: true));
        b.Function("containsCIDR",
            new OverloadDecl("cidr_contains_cidr_string", [cidrT, CelTypes.String], CelTypes.Bool, IsInstance: true),
            new OverloadDecl("cidr_contains_cidr_cidr", [cidrT, cidrT], CelTypes.Bool, IsInstance: true));
        b.Function("ip",
            new OverloadDecl("cidr_to_ip", [cidrT], ipT, IsInstance: true));
        b.Function("prefixLength",
            new OverloadDecl("cidr_prefix_length", [cidrT], CelTypes.Int, IsInstance: true));
        b.Function("masked",
            new OverloadDecl("cidr_masked", [cidrT], cidrT, IsInstance: true));
    }

    public void ConfigureRuntime(Action<string, OverloadFn> bind)
    {
        bind("string_to_ip", static a =>
        {
            var s = ((StringValue)a[0]).Value;
            return TryParseIp(s, out var ip)
                ? new ObjectValue(IpType, ip)
                : CelValue.Error($"IP Address '{s}' parse error during conversion from string");
        });
        bind("string_to_cidr", static a =>
        {
            var s = ((StringValue)a[0]).Value;
            return CelCidr.TryParse(s, out var cidr)
                ? new ObjectValue(CidrType, cidr)
                : CelValue.Error($"CIDR Address '{s}' parse error during conversion from string");
        });
        bind("is_ip_string", static a => CelValue.Of(TryParseIp(((StringValue)a[0]).Value, out _)));
        bind("ip_is_canonical_string", static a =>
        {
            var s = ((StringValue)a[0]).Value;
            if (!TryParseIp(s, out var ip))
            {
                return CelValue.Error($"IP Address '{s}' parse error during conversion from string");
            }
            // Compare against IPAddress's canonical formatting (lowercase, compressed) rather
            // than the original input.
            return CelValue.Of(string.Equals(ip.Address.ToString(), s, StringComparison.Ordinal));
        });

        bind("ip_to_string", static a => CelValue.Of(((CelIp)((ObjectValue)a[0]).Native).Original));
        bind("cidr_to_string", static a => CelValue.Of(((CelCidr)((ObjectValue)a[0]).Native).ToString()));

        bind("ip_family", static a =>
            CelValue.Of(((CelIp)((ObjectValue)a[0]).Native).Address.AddressFamily == AddressFamily.InterNetwork ? 4L : 6L));
        bind("ip_is_unspecified", static a => CelValue.Of(((CelIp)((ObjectValue)a[0]).Native).Address.Equals(Unspecified(((CelIp)((ObjectValue)a[0]).Native).Address.AddressFamily))));
        bind("ip_is_loopback", static a => CelValue.Of(IPAddress.IsLoopback(((CelIp)((ObjectValue)a[0]).Native).Address)));
        bind("ip_is_global_unicast", static a => CelValue.Of(IsGlobalUnicast(((CelIp)((ObjectValue)a[0]).Native).Address)));
        bind("ip_is_link_local_multicast", static a => CelValue.Of(IsLinkLocalMulticast(((CelIp)((ObjectValue)a[0]).Native).Address)));
        bind("ip_is_link_local_unicast", static a => CelValue.Of(IsLinkLocalUnicast(((CelIp)((ObjectValue)a[0]).Native).Address)));

        bind("cidr_contains_ip_string", static a =>
        {
            var cidr = (CelCidr)((ObjectValue)a[0]).Native;
            var s = ((StringValue)a[1]).Value;
            if (!TryParseIp(s, out var ip))
            {
                return CelValue.Error($"IP Address '{s}' parse error");
            }
            return CelValue.Of(cidr.Contains(ip.Canonical));
        });
        bind("cidr_contains_ip_ip", static a =>
        {
            var cidr = (CelCidr)((ObjectValue)a[0]).Native;
            var ip = (CelIp)((ObjectValue)a[1]).Native;
            return CelValue.Of(cidr.Contains(ip.Canonical));
        });
        bind("cidr_contains_cidr_string", static a =>
        {
            var outer = (CelCidr)((ObjectValue)a[0]).Native;
            var s = ((StringValue)a[1]).Value;
            if (!CelCidr.TryParse(s, out var inner))
            {
                return CelValue.Error($"CIDR '{s}' parse error");
            }
            return CelValue.Of(outer.ContainsCidr(inner));
        });
        bind("cidr_contains_cidr_cidr", static a =>
        {
            var outer = (CelCidr)((ObjectValue)a[0]).Native;
            var inner = (CelCidr)((ObjectValue)a[1]).Native;
            return CelValue.Of(outer.ContainsCidr(inner));
        });

        bind("cidr_to_ip", static a => new ObjectValue(IpType, new CelIp(((CelCidr)((ObjectValue)a[0]).Native).Address.ToString(), ((CelCidr)((ObjectValue)a[0]).Native).Address)));
        bind("cidr_prefix_length", static a => CelValue.Of((long)((CelCidr)((ObjectValue)a[0]).Native).PrefixLength));
        bind("cidr_masked", static a =>
        {
            var cidr = (CelCidr)((ObjectValue)a[0]).Native;
            return new ObjectValue(CidrType, cidr.Masked());
        });
    }

    // ── helpers ──

    /// <summary>
    /// Parse an IP address per CEL's network ext rules: rejects scope-id forms (<c>fe80::1%en0</c>)
    /// and rejects IPv6-only-but-malformed inputs that <see cref="IPAddress.TryParse"/> accepts.
    /// </summary>
    private static bool TryParseIp(string s, out CelIp ip)
    {
        ip = default!;
        if (string.IsNullOrEmpty(s)) { return false; }
        if (s.Contains('%', StringComparison.Ordinal))
        {
            return false; // zone identifier — disallowed by cel-go ext/network
        }
        // CEL spec disallows IPv4-mapped IPv6 inputs in dotted form (`::ffff:192.168.0.1`):
        // any string mixing dots and colons is rejected. Pure IPv4 (3 dots) or pure IPv6
        // (no dots) are accepted; dotted IPv4 with the wrong octet count is also rejected
        // because .NET's TryParse silently accepts oddities like "192.168.0.1.0".
        var dots = 0;
        var colons = 0;
        foreach (var c in s)
        {
            if (c == '.') { dots++; }
            else if (c == ':') { colons++; }
        }
        if (dots > 0 && colons > 0) { return false; }
        if (dots != 0 && dots != 3) { return false; }
        if (!IPAddress.TryParse(s, out var parsed))
        {
            return false;
        }
        ip = new CelIp(s, parsed);
        return true;
    }

    private static IPAddress Unspecified(AddressFamily family) =>
        family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;

    private static bool IsGlobalUnicast(IPAddress address)
    {
        // Crude approximation matching cel-go: not loopback / link-local / multicast / unspecified
        // / broadcast. Sufficient for the conformance tests which probe canonical examples.
        if (IPAddress.IsLoopback(address)) { return false; }
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 0.0.0.0
            if (bytes is [0, 0, 0, 0]) { return false; }
            // 255.255.255.255 (broadcast)
            if (bytes is [255, 255, 255, 255]) { return false; }
            // 169.254.0.0/16 link-local
            if (bytes[0] == 169 && bytes[1] == 254) { return false; }
            // 224.0.0.0/4 multicast
            if (bytes[0] >= 224 && bytes[0] < 240) { return false; }
            return true;
        }
        // IPv6
        // ::
        if (Array.TrueForAll(bytes, b => b == 0)) { return false; }
        // ff00::/8 multicast
        if (bytes[0] == 0xFF) { return false; }
        // fe80::/10 link-local unicast
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) { return false; }
        return true;
    }

    private static bool IsLinkLocalMulticast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 224.0.0.0/24 link-local multicast
            return bytes[0] == 224 && bytes[1] == 0 && bytes[2] == 0;
        }
        // IPv6: ff02::/16
        return bytes[0] == 0xFF && bytes[1] == 0x02;
    }

    private static bool IsLinkLocalUnicast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 169.254.0.0/16
            return bytes[0] == 169 && bytes[1] == 254;
        }
        // IPv6: fe80::/10
        return bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
    }
}

/// <summary>
/// IP-address wrapper that preserves the original source string for round-trip formatting and
/// provides a canonicalised form for equality (IPv4-mapped IPv6 addresses canonicalise to the
/// IPv4 form so <c>ip('::ffff:c0a8:1') == ip('192.168.0.1')</c>).
/// </summary>
public sealed class CelIp : IEquatable<CelIp>
{
    public string Original { get; }
    public IPAddress Address { get; }

    public CelIp(string original, IPAddress address)
    {
        Original = original;
        Address = address;
    }

    public IPAddress Canonical =>
        Address.IsIPv4MappedToIPv6 ? Address.MapToIPv4() : Address;

    public bool Equals(CelIp? other) => other is not null && Canonical.Equals(other.Canonical);
    public override bool Equals(object? obj) => obj is CelIp other && Equals(other);
    public override int GetHashCode() => Canonical.GetHashCode();
    public override string ToString() => Original;
}

/// <summary>
/// CIDR wrapper. Equality compares the masked address bytes plus prefix length so e.g.
/// <c>cidr('192.0.0.1/32') != cidr('10.0.0.1/8')</c>.
/// </summary>
public sealed class CelCidr : IEquatable<CelCidr>
{
    public IPAddress Address { get; }
    public int PrefixLength { get; }

    public CelCidr(IPAddress address, int prefixLength)
    {
        Address = address;
        PrefixLength = prefixLength;
    }

    public static bool TryParse(string s, out CelCidr result)
    {
        result = default!;
        if (string.IsNullOrEmpty(s)) { return false; }
        var slash = s.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0) { return false; }
        var ipPart = s[..slash];
        var prefixPart = s[(slash + 1)..];
        if (ipPart.Contains('%', StringComparison.Ordinal)) { return false; }
        // Same dot-count + dotted-IPv6 guards as IP parsing.
        var dots = 0;
        var colons = 0;
        foreach (var c in ipPart)
        {
            if (c == '.') { dots++; }
            else if (c == ':') { colons++; }
        }
        if (dots > 0 && colons > 0) { return false; }
        if (dots != 0 && dots != 3) { return false; }
        if (!IPAddress.TryParse(ipPart, out var ip)) { return false; }
        if (!int.TryParse(prefixPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefix)
            || prefix < 0)
        {
            return false;
        }
        var maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix > maxPrefix) { return false; }
        result = new CelCidr(ip, prefix);
        return true;
    }

    public bool Contains(IPAddress other)
    {
        if (Address.AddressFamily != other.AddressFamily) { return false; }
        var a = Address.GetAddressBytes();
        var b = other.GetAddressBytes();
        return MatchesPrefix(a, b, PrefixLength);
    }

    public bool ContainsCidr(CelCidr other)
    {
        if (Address.AddressFamily != other.Address.AddressFamily) { return false; }
        if (PrefixLength > other.PrefixLength) { return false; }
        return Contains(other.Address);
    }

    public CelCidr Masked()
    {
        var bytes = Address.GetAddressBytes();
        var fullBytes = PrefixLength / 8;
        var remBits = PrefixLength % 8;
        for (var i = fullBytes + (remBits > 0 ? 1 : 0); i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }
        if (remBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remBits));
            bytes[fullBytes] &= mask;
        }
        return new CelCidr(new IPAddress(bytes), PrefixLength);
    }

    private static bool MatchesPrefix(byte[] a, byte[] b, int prefix)
    {
        var fullBytes = prefix / 8;
        var remBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != b[i]) { return false; }
        }
        if (remBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remBits));
            if ((a[fullBytes] & mask) != (b[fullBytes] & mask)) { return false; }
        }
        return true;
    }

    public bool Equals(CelCidr? other)
    {
        if (other is null) { return false; }
        if (PrefixLength != other.PrefixLength) { return false; }
        if (Address.AddressFamily != other.Address.AddressFamily) { return false; }
        var ma = Masked();
        var mb = other.Masked();
        return ma.Address.GetAddressBytes().AsSpan().SequenceEqual(mb.Address.GetAddressBytes());
    }

    public override bool Equals(object? obj) => obj is CelCidr other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Address, PrefixLength);
    public override string ToString() => $"{Address}/{PrefixLength}";
}
