using System.Text.RegularExpressions;

namespace LogixTagRelay;

public static class ExplicitReads
{
    public static void Validate(IReadOnlyList<FieldMap> fields, IReadOnlyList<ExplicitReadGroup> groups)
    {
        var paths = fields.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.Fields is null || group.Fields.Count == 0 || !roots.Add(group.Path) ||
                !Regex.IsMatch(group.Path, @"^(?:Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d+(?:,\d+){0,2}\])?)*$") ||
                !Regex.IsMatch(group.DataType, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new FormatException("Invalid explicit UDT read group.");
            foreach (string path in group.Fields)
                if (!paths.Contains(path) || !path.StartsWith(group.Path + ".", StringComparison.OrdinalIgnoreCase) || !covered.Add(path))
                    throw new FormatException("UDT read groups must cover distinct mapped member paths under their root.");
        }
    }
}
