namespace LogixTagRelay.Design;

/// <summary>Accessible application members of classic Logix timer/counter structures.</summary>
public static class BuiltInTypes
{
    public static bool IsTimerOrCounter(string type) => type is "TIMER" or "COUNTER";
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
