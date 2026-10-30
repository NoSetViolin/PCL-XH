using System;
using System.Linq;
using System.Numerics;
using System.Globalization;

namespace PCL.Core.Updates;

/// <summary>Numeric release tags; omitted trailing components are zero.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly BigInteger[] _parts;
    private readonly string _numericTag;
    public string Tag { get; }
    public ReleaseChannel? ExplicitChannel { get; }
    public bool IsDeveloperBuild
    {
        get
        {
            var parts = _numericTag.Split('.');
            return parts.Length == 2 && parts[0].Length == 2 && parts[1].Length == 5 && parts[1][0] == '0' &&
                DateTime.TryParseExact("20" + parts[0] + parts[1][1..], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _);
        }
    }
    private ReleaseVersion(string tag, string numericTag, BigInteger[] parts, ReleaseChannel? channel)
    { Tag = tag; _numericTag = numericTag; _parts = parts; ExplicitChannel = channel; }

    public static bool TryParse(string? tag, out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var value = tag.Trim();
        var displayTag = value;
        ReleaseChannel? channel = null;
        if (value.StartsWith("dev-", StringComparison.OrdinalIgnoreCase)) { channel = ReleaseChannel.DeveloperBeta; value = value[4..]; }
        else if (value.StartsWith("beta-", StringComparison.OrdinalIgnoreCase)) { channel = ReleaseChannel.PublicBeta; value = value[5..]; }
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var parts = value.Split('.');
        if (parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9'))) return false;
        version = new ReleaseVersion(channel.HasValue ? displayTag : value, value, parts.Select(BigInteger.Parse).ToArray(), channel);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < Math.Max(_parts.Length, other._parts.Length); i++)
        {
            var comparison = (i < _parts.Length ? _parts[i] : BigInteger.Zero)
                .CompareTo(i < other._parts.Length ? other._parts[i] : BigInteger.Zero);
            if (comparison != 0) return comparison;
        }
        return 0;
    }
    public override string ToString() => Tag;
}
