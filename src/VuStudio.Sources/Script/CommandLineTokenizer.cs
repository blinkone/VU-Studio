using System.Text;

namespace VuStudio.Sources.Script;

/// <summary>
/// Splits a command line into program and arguments for the "none" shell (direct execution).
/// Whitespace separates words; <c>"double"</c> and <c>'single'</c> quotes group words. Backslashes are literal
/// (so <c>C:\Tools\app.exe</c> works unquoted) except in front of a double quote, where the usual Windows
/// rules apply: <c>\"</c> is a literal quote and <c>\\"</c> is a backslash followed by a closing quote.
/// </summary>
internal static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var quote = '\0';

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (quote == '\0' && char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            inToken = true;
            if (c == '\\' && quote != '\'')
            {
                var run = 1;
                while (i + run < commandLine.Length && commandLine[i + run] == '\\')
                {
                    run++;
                }

                if (i + run < commandLine.Length && commandLine[i + run] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1)
                    {
                        current.Append('"');
                        i += run; // the backslashes and the escaped quote
                    }
                    else
                    {
                        i += run - 1; // the quote itself is handled below, as a delimiter
                    }
                }
                else
                {
                    current.Append('\\', run);
                    i += run - 1;
                }

                continue;
            }

            if (c == '"' && quote != '\'')
            {
                quote = quote == '"' ? '\0' : '"';
            }
            else if (c == '\'' && quote != '"')
            {
                quote = quote == '\'' ? '\0' : '\'';
            }
            else
            {
                current.Append(c);
            }
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
