namespace LogixTagRelay.Design;

/// <summary>Accessible application members of classic Logix timer/counter structures.</summary>
public static class BuiltInTypes
{
    /// <summary>Whether the native type is a supported classic TIMER or COUNTER.</summary>
    /// <param name="type">Native Logix type name to recognize.</param>
    public static bool IsTimerOrCounter(string type) => type is "TIMER" or "COUNTER";
    /// <summary>Builds a catalog node for a supported classic TIMER or COUNTER.</summary>
    /// <param name="name">Catalog display name of the tag or member.</param>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="type">Classic TIMER or COUNTER type name.</param>
    /// <param name="access">Controller external access metadata to preserve in expanded members.</param>
    public static TagNode Expand(string name, string path, string type, string access)
    {
        if (!IsTimerOrCounter(type)) throw new ArgumentException("Expected TIMER or COUNTER.");
        var children = new List<TagNode>();
        foreach (string member in new[] { "PRE", "ACC" }) children.Add(new(member, path + "." + member, "DINT", access, [], UnsupportedReason: access == "None" ? "ExternalAccess=None" : null));
        foreach (string member in type == "TIMER" ? new[] { "EN", "TT", "DN" } : new[] { "CU", "CD", "DN", "OV", "UN" })
            children.Add(new(member, path + "." + member, "BOOL", access == "None" ? "None" : access == "Unknown" ? "Unknown" : "Read Only", [], UnsupportedReason: access == "None" ? "ExternalAccess=None" : null));
        return new(name, path, type, access, children);
    }
}
