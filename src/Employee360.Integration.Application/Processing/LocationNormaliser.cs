using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Employee360.Integration.Application.Processing;

public sealed class ReferenceDataOptions
{
    public const string SectionName = "ReferenceData";

    /// <summary>Canonical location name mapped to the aliases source systems use for it.</summary>
    public Dictionary<string, string[]> Locations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Maps source location values (CPT, cape town, Kaapstad...) to the canonical value Employee 360 expects.
/// Unknown values are reported as unmapped rather than passed through, so bad reference data
/// is quarantined instead of polluting the golden record. In OCS this lookup would come from
/// Profisee MDM rather than configuration.
/// </summary>
public sealed class LocationNormaliser
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private readonly Dictionary<string, string> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public LocationNormaliser(IOptions<ReferenceDataOptions> options)
    {
        foreach (var (canonical, aliases) in options.Value.Locations)
        {
            var canonicalName = Clean(canonical);
            _lookup[canonicalName] = canonicalName;

            foreach (var alias in aliases)
            {
                _lookup[Clean(alias)] = canonicalName;
            }
        }
    }

    public bool TryNormalise(string rawValue, [NotNullWhen(true)] out string? canonical) =>
        _lookup.TryGetValue(Clean(rawValue), out canonical);

    private static string Clean(string value) => Whitespace.Replace(value.Trim(), " ");
}
