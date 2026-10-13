using System.Text;

namespace VuStudio.Core.Faces;

/// <summary>Resolves template placeholders.</summary>
public interface ITemplateResolver
{
    /// <summary>
    /// Resolves <c>{name:arg|format}</c>. Returns null for unknown names (the placeholder is then kept verbatim).
    /// </summary>
    string? Resolve(string name, string? argument, string? format);
}

/// <summary>
/// Minimal template language used by face text, icons and alarm messages.
/// <list type="bullet">
/// <item><c>{value}</c>, <c>{unit}</c>, <c>{title}</c>, <c>{subtitle}</c>, <c>{min}</c>, <c>{max}</c>, <c>{percent}</c>, <c>{icon}</c></item>
/// <item><c>{time:HH:mm}</c>, <c>{date:ddd d MMM}</c> — .NET date formats</item>
/// <item><c>{sensor:&lt;id&gt;}</c> number + unit (or text); <c>{sensor.value:&lt;id&gt;}</c>; <c>{sensor.unit:&lt;id&gt;}</c>; <c>{sensor.name:&lt;id&gt;}</c></item>
/// <item>A trailing <c>|format</c> applies a .NET numeric format to numbers, e.g. <c>{value|0.0}</c>.</item>
/// <item><c>{{</c> and <c>}}</c> are literal braces.</item>
/// </list>
/// </summary>
public static class TextTemplate
{
    public static string Render(string? template, ITemplateResolver resolver)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        if (template.IndexOf('{') < 0 && template.IndexOf('}') < 0)
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        foreach (var token in Tokenize(template))
        {
            if (token.IsLiteral)
            {
                sb.Append(token.Text);
                continue;
            }

            var resolved = resolver.Resolve(token.Name!, token.Argument, token.Format);
            sb.Append(resolved ?? "{" + token.Text + "}");
        }

        return sb.ToString();
    }

    /// <summary>Sensor ids referenced by <c>{sensor…:&lt;id&gt;}</c> placeholders.</summary>
    public static IEnumerable<string> ReferencedSensors(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            yield break;
        }

        foreach (var token in Tokenize(template))
        {
            if (!token.IsLiteral && token.Name!.StartsWith("sensor", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(token.Argument))
            {
                yield return token.Argument!;
            }
        }
    }

    /// <summary>True when the template references something that changes over time.</summary>
    public static bool IsDynamic(string? template) =>
        !string.IsNullOrEmpty(template) && Tokenize(template).Any(t => !t.IsLiteral && t.Name is not ("title" or "subtitle" or "unit" or "min" or "max" or "icon"));

    private readonly record struct Token(bool IsLiteral, string Text, string? Name, string? Argument, string? Format);

    private static IEnumerable<Token> Tokenize(string template)
    {
        var literal = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                literal.Append('{');
                i += 2;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                literal.Append('}');
                i += 2;
                continue;
            }

            if (c == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    literal.Append(template, i, template.Length - i);
                    break;
                }

                if (literal.Length > 0)
                {
                    yield return new Token(true, literal.ToString(), null, null, null);
                    literal.Clear();
                }

                var body = template.Substring(i + 1, end - i - 1);
                yield return Parse(body);
                i = end + 1;
                continue;
            }

            literal.Append(c);
            i++;
        }

        if (literal.Length > 0)
        {
            yield return new Token(true, literal.ToString(), null, null, null);
        }
    }

    private static Token Parse(string body)
    {
        string? format = null;
        var pipe = body.LastIndexOf('|');
        var head = body;
        if (pipe >= 0)
        {
            format = body[(pipe + 1)..];
            head = body[..pipe];
        }

        string? argument = null;
        var colon = head.IndexOf(':');
        var name = head;
        if (colon >= 0)
        {
            argument = head[(colon + 1)..];
            name = head[..colon];
        }

        return new Token(false, body, name.Trim().ToLowerInvariant(), argument, string.IsNullOrEmpty(format) ? null : format);
    }
}
