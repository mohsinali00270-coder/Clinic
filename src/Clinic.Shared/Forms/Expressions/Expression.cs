using System.Collections.Concurrent;
using System.Globalization;

namespace Clinic.Shared.Forms.Expressions;

/// <summary>A syntax or evaluation error, with the character position when known.</summary>
public sealed class ExpressionException(string message, int position = -1)
    : Exception(position >= 0 ? $"{message} (at position {position + 1})" : message)
{
    public int Position { get; } = position;
}

/// <summary>
/// FR-DYN-03/04: the safe expression language used for conditions, calculations and abnormal rules.
/// <para>
/// Literals: numbers, 'strings' or "strings", true, false, null. Identifiers: field keys and context variables.
/// Operators: + - * / % ^, == != &lt; &lt;= &gt; &gt;=, &amp;&amp; || ! (also and / or / not).
/// Functions: if, round, floor, ceil, abs, min, max, sqrt, pow, len, isEmpty, contains, coalesce, number, text,
/// today, now, daysBetween, ageYears, ageMonths, year, month, day, addDays.
/// </para>
/// <para>
/// Missing values are null; arithmetic on null gives null (so BMI stays blank until weight and height exist);
/// comparisons with null are false except == / !=. date - date = days; date + n = date n days later.
/// No loops, no reflection, no I/O: evaluation is bounded by the size of the expression.
/// </para>
/// </summary>
public sealed class CompiledExpression
{
    internal CompiledExpression(string source, Node root, IReadOnlySet<string> identifiers)
    {
        Source = source;
        Root = root;
        Identifiers = identifiers;
    }

    public string Source { get; }
    internal Node Root { get; }

    /// <summary>Every identifier the expression reads (field keys / variables), for validation and dependencies.</summary>
    public IReadOnlySet<string> Identifiers { get; }

    public object? Evaluate(Func<string, object?> resolve, DateTime? today = null) =>
        Evaluator.Evaluate(Root, new EvalScope(resolve, today ?? DateTime.Today));

    public bool EvaluateBool(Func<string, object?> resolve, DateTime? today = null) =>
        Evaluator.Truthy(Evaluate(resolve, today));
}

public static class ExpressionCompiler
{
    public const int MaxLength = 2000;
    private static readonly ConcurrentDictionary<string, CompiledExpression> Cache = new();

    /// <summary>Parses once and caches by source text.</summary>
    public static CompiledExpression Compile(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ExpressionException("The expression is empty.");
        if (source.Length > MaxLength) throw new ExpressionException($"The expression is longer than {MaxLength} characters.");

        return Cache.GetOrAdd(source, static s =>
        {
            var parser = new Parser(Lexer.Tokenize(s));
            var root = parser.ParseAll();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            Collect(root, ids);
            return new CompiledExpression(s, root, ids);
        });
    }

    public static bool TryCompile(string source, out CompiledExpression? expression, out string? error)
    {
        try
        {
            expression = Compile(source);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    private static void Collect(Node node, HashSet<string> ids)
    {
        switch (node)
        {
            case IdentNode i: ids.Add(i.Name); break;
            case UnaryNode u: Collect(u.Operand, ids); break;
            case BinaryNode b: Collect(b.Left, ids); Collect(b.Right, ids); break;
            case CallNode c: foreach (var a in c.Args) Collect(a, ids); break;
        }
    }

    public static IReadOnlyCollection<string> FunctionNames => Evaluator.Functions;
}

// =========================================================================
// Lexer
// =========================================================================

internal enum TokenKind { Number, String, Identifier, Operator, LParen, RParen, Comma, End }

internal readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0);

internal static class Lexer
{
    private static readonly string[] Operators = ["&&", "||", "==", "!=", "<=", ">=", "<", ">", "+", "-", "*", "/", "%", "^", "!"];

    public static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
            {
                var start = i;
                while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                var text = s[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    throw new ExpressionException($"'{text}' is not a valid number", start);
                tokens.Add(new Token(TokenKind.Number, text, start, n));
                continue;
            }

            if (c is '\'' or '"')
            {
                var start = i++;
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw new ExpressionException("Unclosed text: missing closing quote", start);
                    if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                    if (s[i] == c) { i++; break; }
                    sb.Append(s[i++]);
                }
                tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.')) i++;
                var word = s[start..i];
                switch (word)
                {
                    case "and": tokens.Add(new Token(TokenKind.Operator, "&&", start)); break;
                    case "or": tokens.Add(new Token(TokenKind.Operator, "||", start)); break;
                    case "not": tokens.Add(new Token(TokenKind.Operator, "!", start)); break;
                    default: tokens.Add(new Token(TokenKind.Identifier, word, start)); break;
                }
                continue;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(TokenKind.LParen, "(", i++)); continue;
                case ')': tokens.Add(new Token(TokenKind.RParen, ")", i++)); continue;
                case ',': tokens.Add(new Token(TokenKind.Comma, ",", i++)); continue;
            }

            var op = Operators.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
            if (op is null)
            {
                if (c == '=') throw new ExpressionException("Use == to compare values", i);
                if (c == '&' || c == '|') throw new ExpressionException($"Use {c}{c} for 'and'/'or'", i);
                throw new ExpressionException($"Unexpected character '{c}'", i);
            }
            tokens.Add(new Token(TokenKind.Operator, op, i));
            i += op.Length;
        }
        tokens.Add(new Token(TokenKind.End, "", s.Length));
        return tokens;
    }
}

// =========================================================================
// AST + Pratt parser
// =========================================================================

internal abstract record Node(int Position);
internal sealed record LiteralNode(object? Value, int Position) : Node(Position);
internal sealed record IdentNode(string Name, int Position) : Node(Position);
internal sealed record UnaryNode(string Op, Node Operand, int Position) : Node(Position);
internal sealed record BinaryNode(string Op, Node Left, Node Right, int Position) : Node(Position);
internal sealed record CallNode(string Name, IReadOnlyList<Node> Args, int Position) : Node(Position);

internal sealed class Parser(List<Token> tokens)
{
    private const int MaxDepth = 64;
    private int _pos;
    private int _depth;

    private Token Current => tokens[_pos];

    private static int Precedence(string op) => op switch
    {
        "||" => 1,
        "&&" => 2,
        "==" or "!=" => 3,
        "<" or "<=" or ">" or ">=" => 4,
        "+" or "-" => 5,
        "*" or "/" or "%" => 6,
        "^" => 7,
        _ => 0,
    };

    public Node ParseAll()
    {
        var node = ParseExpression(0);
        if (Current.Kind != TokenKind.End)
            throw new ExpressionException($"Unexpected '{Current.Text}'", Current.Position);
        return node;
    }

    private Node ParseExpression(int minPrecedence)
    {
        if (++_depth > MaxDepth) throw new ExpressionException("The expression is nested too deeply.");
        var left = ParseUnary();

        while (Current.Kind == TokenKind.Operator && Precedence(Current.Text) > minPrecedence)
        {
            var op = Current;
            _pos++;
            // ^ is right-associative; everything else left-associative.
            var nextMin = op.Text == "^" ? Precedence(op.Text) - 1 : Precedence(op.Text);
            var right = ParseExpression(nextMin);
            left = new BinaryNode(op.Text, left, right, op.Position);
        }

        _depth--;
        return left;
    }

    private Node ParseUnary()
    {
        if (Current is { Kind: TokenKind.Operator, Text: "!" or "-" or "+" })
        {
            var op = Current;
            _pos++;
            if (++_depth > MaxDepth) throw new ExpressionException("The expression is nested too deeply.");
            // Unary binds tighter than * but looser than ^ (so -2^2 = -4).
            var operand = ParseExpression(Precedence("*"));
            _depth--;
            return op.Text == "+" ? operand : new UnaryNode(op.Text, operand, op.Position);
        }
        return ParsePrimary();
    }

    private Node ParsePrimary()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Number:
                _pos++;
                return new LiteralNode(t.Number, t.Position);

            case TokenKind.String:
                _pos++;
                return new LiteralNode(t.Text, t.Position);

            case TokenKind.Identifier:
                _pos++;
                switch (t.Text)
                {
                    case "true": return new LiteralNode(true, t.Position);
                    case "false": return new LiteralNode(false, t.Position);
                    case "null": return new LiteralNode(null, t.Position);
                }
                if (Current.Kind != TokenKind.LParen) return new IdentNode(t.Text, t.Position);

                if (!Evaluator.Functions.Contains(t.Text))
                    throw new ExpressionException($"Unknown function '{t.Text}'", t.Position);
                _pos++;
                var args = new List<Node>();
                if (Current.Kind != TokenKind.RParen)
                {
                    while (true)
                    {
                        args.Add(ParseExpression(0));
                        if (Current.Kind == TokenKind.Comma) { _pos++; continue; }
                        break;
                    }
                }
                Expect(TokenKind.RParen, "')' to close the function call");
                Evaluator.CheckArity(t.Text, args.Count, t.Position);
                return new CallNode(t.Text, args, t.Position);

            case TokenKind.LParen:
                _pos++;
                var inner = ParseExpression(0);
                Expect(TokenKind.RParen, "')'");
                return inner;

            case TokenKind.End:
                throw new ExpressionException("The expression ends unexpectedly", t.Position);

            default:
                throw new ExpressionException($"Unexpected '{t.Text}'", t.Position);
        }
    }

    private void Expect(TokenKind kind, string what)
    {
        if (Current.Kind != kind) throw new ExpressionException($"Expected {what}", Current.Position);
        _pos++;
    }
}

// =========================================================================
// Evaluator
// =========================================================================

internal sealed record EvalScope(Func<string, object?> Resolve, DateTime Today);

internal static class Evaluator
{
    private delegate object? Fn(object?[] a, EvalScope s);

    private static readonly Dictionary<string, (int Min, int Max, Fn Body)> Table = new(StringComparer.Ordinal)
    {
        ["round"] = (1, 2, (a, _) => Num(a[0]) is { } x ? Math.Round(x, a.Length > 1 ? (int)(Num(a[1]) ?? 0) : 0, MidpointRounding.AwayFromZero) : null),
        ["floor"] = (1, 1, (a, _) => Num(a[0]) is { } x ? Math.Floor(x) : null),
        ["ceil"] = (1, 1, (a, _) => Num(a[0]) is { } x ? Math.Ceiling(x) : null),
        ["abs"] = (1, 1, (a, _) => Num(a[0]) is { } x ? Math.Abs(x) : null),
        ["sqrt"] = (1, 1, (a, _) => Num(a[0]) is { } x && x >= 0 ? Math.Sqrt(x) : null),
        ["pow"] = (2, 2, (a, _) => Num(a[0]) is { } x && Num(a[1]) is { } y ? Finite(Math.Pow(x, y)) : null),
        ["min"] = (1, 20, (a, _) => a.Select(Num).OfType<double>().DefaultIfEmpty(double.NaN).Min() is var m && !double.IsNaN(m) ? m : null),
        ["max"] = (1, 20, (a, _) => a.Select(Num).OfType<double>().DefaultIfEmpty(double.NaN).Max() is var m && !double.IsNaN(m) ? m : null),
        ["len"] = (1, 1, (a, _) => a[0] switch { null => 0d, string s => s.Length, System.Collections.ICollection c => c.Count, _ => 1d }),
        ["isEmpty"] = (1, 1, (a, _) => IsEmpty(a[0])),
        ["contains"] = (2, 2, (a, _) => a[0] switch
        {
            null => false,
            string s => a[1] is not null && s.Contains(Text(a[1])!, StringComparison.OrdinalIgnoreCase),
            IEnumerable<string> list => a[1] is not null && list.Contains(Text(a[1])!, StringComparer.OrdinalIgnoreCase),
            _ => false,
        }),
        ["coalesce"] = (1, 20, (a, _) => a.FirstOrDefault(x => !IsEmpty(x))),
        ["number"] = (1, 1, (a, _) => Num(a[0])),
        ["text"] = (1, 1, (a, _) => Text(a[0])),
        ["today"] = (0, 0, (_, s) => s.Today.Date),
        ["now"] = (0, 0, (_, _) => DateTime.Now),
        ["daysBetween"] = (2, 2, (a, _) => Date(a[0]) is { } x && Date(a[1]) is { } y ? (y.Date - x.Date).TotalDays : null),
        ["ageYears"] = (1, 1, (a, s) => Date(a[0]) is { } d ? AgeYears(d, s.Today) : null),
        ["ageMonths"] = (1, 1, (a, s) => Date(a[0]) is { } d ? AgeMonths(d, s.Today) : null),
        ["year"] = (1, 1, (a, _) => Date(a[0]) is { } d ? d.Year : null),
        ["month"] = (1, 1, (a, _) => Date(a[0]) is { } d ? d.Month : null),
        ["day"] = (1, 1, (a, _) => Date(a[0]) is { } d ? d.Day : null),
        ["addDays"] = (2, 2, (a, _) => Date(a[0]) is { } d && Num(a[1]) is { } n ? d.AddDays(n) : null),
        ["if"] = (3, 3, (_, _) => throw new InvalidOperationException("if is evaluated lazily")),
    };

    public static IReadOnlyCollection<string> Functions => Table.Keys;

    public static void CheckArity(string name, int count, int position)
    {
        var (min, max, _) = Table[name];
        if (count < min || count > max)
        {
            var expected = min == max ? $"{min}" : $"{min} to {max}";
            throw new ExpressionException($"{name}() takes {expected} argument(s), not {count}", position);
        }
    }

    public static object? Evaluate(Node node, EvalScope scope) => node switch
    {
        LiteralNode l => l.Value,
        IdentNode i => Normalize(scope.Resolve(i.Name)),
        UnaryNode u => u.Op == "!" ? !Truthy(Evaluate(u.Operand, scope)) : Num(Evaluate(u.Operand, scope)) is { } n ? -n : null,
        BinaryNode b => Binary(b, scope),
        CallNode c => Call(c, scope),
        _ => throw new ExpressionException("Unsupported expression"),
    };

    private static object? Call(CallNode c, EvalScope scope)
    {
        if (c.Name == "if")
            return Truthy(Evaluate(c.Args[0], scope)) ? Evaluate(c.Args[1], scope) : Evaluate(c.Args[2], scope);

        var args = c.Args.Select(a => Evaluate(a, scope)).ToArray();
        return Normalize(Table[c.Name].Body(args, scope));
    }

    private static object? Binary(BinaryNode b, EvalScope scope)
    {
        // Short-circuit logic.
        if (b.Op == "&&") return Truthy(Evaluate(b.Left, scope)) && Truthy(Evaluate(b.Right, scope));
        if (b.Op == "||") return Truthy(Evaluate(b.Left, scope)) || Truthy(Evaluate(b.Right, scope));

        var l = Evaluate(b.Left, scope);
        var r = Evaluate(b.Right, scope);

        switch (b.Op)
        {
            case "==": return AreEqual(l, r);
            case "!=": return !AreEqual(l, r);
            case "<" or "<=" or ">" or ">=":
                var cmp = Compare(l, r);
                if (cmp is null) return false;
                return b.Op switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 };
        }

        if (l is null || r is null) return null;

        // Dates: date ± days, date - date = days.
        if (l is DateTime dl)
        {
            if (b.Op == "-" && r is DateTime dr) return (dl - dr).TotalDays;
            if (b.Op is "+" or "-" && Num(r) is { } days) return dl.AddDays(b.Op == "+" ? days : -days);
        }

        if (b.Op == "+" && (l is string || r is string)) return Text(l) + Text(r);

        var x = Num(l);
        var y = Num(r);
        if (x is null || y is null) return null;

        return b.Op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => y == 0 ? null : x / y,
            "%" => y == 0 ? null : x % y,
            "^" => Finite(Math.Pow(x.Value, y.Value)),
            _ => throw new ExpressionException($"Unknown operator '{b.Op}'", b.Position),
        };
    }

    // ---- value helpers -------------------------------------------------

    /// <summary>Numbers become double; everything else passes through.</summary>
    public static object? Normalize(object? v) => v switch
    {
        null => null,
        double d => Finite(d),
        int i => (double)i,
        long l => (double)l,
        decimal m => (double)m,
        float f => Finite(f),
        DateTimeOffset o => o.DateTime,
        _ => v,
    };

    private static object? Finite(double d) => double.IsFinite(d) ? d : null;

    public static double? Num(object? v) => v switch
    {
        null => null,
        double d => d,
        int i => i,
        long l => l,
        decimal m => (double)m,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public static string? Text(object? v) => v switch
    {
        null => null,
        string s => s,
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("s", CultureInfo.InvariantCulture),
        IEnumerable<string> list => string.Join(", ", list),
        _ => v.ToString(),
    };

    public static DateTime? Date(object? v) => v switch
    {
        DateTime d => d,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) => p,
        _ => null,
    };

    public static bool IsEmpty(object? v) => v switch
    {
        null => true,
        string s => string.IsNullOrWhiteSpace(s),
        System.Collections.ICollection c => c.Count == 0,
        _ => false,
    };

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    private static bool AreEqual(object? l, object? r)
    {
        if (l is null || r is null) return IsEmpty(l) && IsEmpty(r);
        if (Num(l) is { } x && Num(r) is { } y && (l is double || r is double)) return Math.Abs(x - y) < 1e-9;
        if (l is DateTime || r is DateTime) return Date(l) is { } a && Date(r) is { } b && a == b;
        if (l is bool || r is bool) return Truthy(l) == Truthy(r);
        return string.Equals(Text(l), Text(r), StringComparison.OrdinalIgnoreCase);
    }

    private static int? Compare(object? l, object? r)
    {
        if (l is null || r is null) return null;
        if (l is DateTime || r is DateTime)
            return Date(l) is { } a && Date(r) is { } b ? a.CompareTo(b) : null;
        if (Num(l) is { } x && Num(r) is { } y) return x.CompareTo(y);
        return string.Compare(Text(l), Text(r), StringComparison.OrdinalIgnoreCase);
    }

    private static double AgeYears(DateTime dob, DateTime today)
    {
        var age = today.Year - dob.Year;
        if (today.Date < dob.Date.AddYears(age)) age--;
        return Math.Max(0, age);
    }

    private static double AgeMonths(DateTime dob, DateTime today)
    {
        var months = (today.Year - dob.Year) * 12 + today.Month - dob.Month;
        if (today.Day < dob.Day) months--;
        return Math.Max(0, months);
    }
}
