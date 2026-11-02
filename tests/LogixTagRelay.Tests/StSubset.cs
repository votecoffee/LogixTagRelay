using System.Text.RegularExpressions;

// Executes the emitted AOI source itself in a deliberately limited offline ST subset.
// This is semantic/model evidence, not a Logix compiler or native acceptance claim.
internal sealed class StSubset
{
    internal sealed class Machine(sbyte[] last, sbyte[] current, sbyte[] received)
    {
        public Dictionary<string, sbyte[]> Arrays { get; } = new(StringComparer.OrdinalIgnoreCase)
            { ["LastSent"] = last, ["Current"] = current, ["Received"] = received };
        public Dictionary<string, long> Scalars { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Get(string name) => Scalars.GetValueOrDefault(name);
    }
    private readonly string[] tokens;
    private int position;
    private delegate long Expr(Machine machine);
    private delegate void Statement(Machine machine);
    private readonly Statement program;
    public StSubset(string source)
    {
        string clean = Regex.Replace(source, @"//[^\n]*", "");
        var matches = Regex.Matches(clean, @"\s+|:=|<>|<=|>=|[A-Za-z_]\w*|\d+|[\[\](),;+\-=<>]");
        if (string.Concat(matches.Select(m => m.Value)) != clean) throw new FormatException("Unsupported ST token.");
        tokens = matches.Select(m => m.Value).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        program = Block();
        if (position != tokens.Length) throw new FormatException("Unparsed ST source.");
    }
    public Machine Run(sbyte[] last, sbyte[] current, sbyte[] received)
    { var m = new Machine(last, current, received); program(m); return m; }
    private string Peek => position < tokens.Length ? tokens[position] : "";
    private bool Take(string token)
    { if (!Peek.Equals(token, StringComparison.OrdinalIgnoreCase)) return false; position++; return true; }
    private string Next() => position < tokens.Length ? tokens[position++] : throw new FormatException("Unexpected ST end.");
    private void Expect(string token) { if (!Take(token)) throw new FormatException($"Expected {token}, found {Peek}."); }
    private Statement Block(params string[] end)
    {
        var statements = new List<Statement>();
        while (position < tokens.Length && !end.Contains(Peek, StringComparer.OrdinalIgnoreCase)) statements.Add(ParseStatement());
        return m => { foreach (var statement in statements) statement(m); };
    }
    private Statement ParseStatement()
    {
        if (Take("IF"))
        {
            var condition = Expression(); Expect("THEN");
            var yes = Block("ELSE", "END_IF");
            Statement no = _ => { };
            if (Take("ELSE")) no = Block("END_IF");
            Expect("END_IF"); Expect(";");
            return m => { if (condition(m) != 0) yes(m); else no(m); };
        }
        if (Take("FOR"))
        {
            string name = Next(); Expect(":="); var first = Expression(); Expect("TO"); var last = Expression(); Expect("DO");
            var body = Block("END_FOR"); Expect("END_FOR"); Expect(";");
            return m =>
            {
                long start = first(m), finish = last(m);
                if (finish - start > 1000000) throw new InvalidOperationException("Offline loop bound exceeded.");
                for (long i = start; i <= finish; i++) { m.Scalars[name] = i; body(m); }
            };
        }
        if (Take("SIZE"))
        {
            Expect("("); string array = Next(); Expect(","); Expect("0"); Expect(","); string dest = Next(); Expect(")"); Expect(";");
            return m => m.Scalars[dest] = m.Arrays[array].Length;
        }
        string target = Next(); Expr? index = null;
        if (Take("[")) { index = Expression(); Expect("]"); }
        Expect(":="); var value = Expression(); Expect(";");
        return m => { if (index is null) m.Scalars[target] = value(m); else m.Arrays[target][checked((int)index(m))] = checked((sbyte)value(m)); };
    }
    private Expr Expression() => Or();
    private Expr Or()
    {
        var value = And();
        while (Take("OR")) { var left = value; var right = And(); value = m => left(m) != 0 || right(m) != 0 ? 1 : 0; }
        return value;
    }
    private Expr And()
    {
        var value = Compare();
        while (Take("AND")) { var left = value; var right = Compare(); value = m => left(m) != 0 && right(m) != 0 ? 1 : 0; }
        return value;
    }
    private Expr Compare()
    {
        var left = Add();
        if (!new[] { "=", "<>", "<", ">", "<=", ">=" }.Contains(Peek)) return left;
        string op = Next(); var right = Add();
        return m => (op switch { "=" => left(m) == right(m), "<>" => left(m) != right(m), "<" => left(m) < right(m),
            ">" => left(m) > right(m), "<=" => left(m) <= right(m), ">=" => left(m) >= right(m), _ => false }) ? 1 : 0;
    }
    private Expr Add()
    {
        var value = Atom();
        while (Peek is "+" or "-") { string op = Next(); var left = value; var right = Atom(); value = m => op == "+" ? left(m) + right(m) : left(m) - right(m); }
        return value;
    }
    private Expr Atom()
    {
        if (Take("NOT")) { var value = Atom(); return m => value(m) == 0 ? 1 : 0; }
        if (Take("-")) { var value = Atom(); return m => -value(m); }
        if (Take("(")) { var value = Expression(); Expect(")"); return value; }
        string name = Next();
        if (long.TryParse(name, out long number)) return _ => number;
        if (!Take("[")) return m => m.Get(name);
        var index = Expression(); Expect("]");
        return m => m.Arrays[name][checked((int)index(m))];
    }
}
