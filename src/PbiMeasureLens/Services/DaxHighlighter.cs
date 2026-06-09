using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PbiMeasureLens.Services;

/// <summary>
/// Lightweight, dependency-free DAX syntax highlighter. Tokenises an expression and renders it
/// into a WPF <see cref="FlowDocument"/> with colours approximating the Power BI DAX editor.
/// </summary>
public static class DaxHighlighter
{
    private static readonly Brush Comment = Hex("#1E7A34");   // green
    private static readonly Brush StringLit = Hex("#A31515"); // dark red
    private static readonly Brush Number = Hex("#0E8A6B");    // teal-green
    private static readonly Brush Function = Hex("#1F61C7");  // blue
    private static readonly Brush Keyword = Hex("#1F61C7");   // blue (bold)
    private static readonly Brush Reference = Hex("#267F99"); // teal — [col]/[measure] and 'table'
    private static readonly Brush Plain = Hex("#2B2B2B");
    private static readonly Brush HeaderBrush = Hex("#1A1A1A");
    private static readonly Brush NoteBrush = Hex("#B26A00"); // amber

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAR", "RETURN", "IN", "NOT", "EVALUATE", "DEFINE", "MEASURE", "COLUMN",
        "TABLE", "ORDER", "BY", "START", "AT", "ASC", "DESC", "TRUE", "FALSE",
    };

    /// <summary>A measure: bold header line, an optional amber note, then the highlighted DAX.</summary>
    public static FlowDocument BuildMeasure(string header, string dax, string? note = null)
    {
        var doc = NewDoc();
        doc.Blocks.Add(new Paragraph(new Run(header) { FontWeight = FontWeights.SemiBold, Foreground = HeaderBrush })
        {
            Margin = new Thickness(0, 0, 0, 6)
        });
        if (!string.IsNullOrEmpty(note))
            doc.Blocks.Add(new Paragraph(new Run(note) { Foreground = NoteBrush, FontStyle = FontStyles.Italic })
            {
                Margin = new Thickness(0, 0, 0, 6)
            });
        doc.Blocks.Add(HighlightParagraph(dax ?? ""));
        return doc;
    }

    /// <summary>A plain informational message (no highlighting).</summary>
    public static FlowDocument BuildMessage(string text)
    {
        var doc = NewDoc();
        doc.Blocks.Add(new Paragraph(new Run(text) { Foreground = Plain }));
        return doc;
    }

    private static Paragraph HighlightParagraph(string dax)
    {
        var para = new Paragraph { Margin = new Thickness(0) };
        foreach (var (text, brush, bold, italic) in Tokenize(dax))
        {
            // Split on newlines so the FlowDocument renders real line breaks.
            string[] lines = text.Replace("\r", "").Split('\n');
            for (int k = 0; k < lines.Length; k++)
            {
                if (k > 0) para.Inlines.Add(new LineBreak());
                if (lines[k].Length == 0) continue;
                var run = new Run(lines[k]) { Foreground = brush };
                if (bold) run.FontWeight = FontWeights.SemiBold;
                if (italic) run.FontStyle = FontStyles.Italic;
                para.Inlines.Add(run);
            }
        }
        return para;
    }

    private readonly record struct Token(string Text, Brush Brush, bool Bold, bool Italic);

    private static IEnumerable<Token> Tokenize(string s)
    {
        int i = 0, n = s.Length;
        var plain = new System.Text.StringBuilder();

        IEnumerable<Token> FlushPlain()
        {
            if (plain.Length == 0) yield break;
            yield return new Token(plain.ToString(), Plain, false, false);
            plain.Clear();
        }

        while (i < n)
        {
            char c = s[i];
            char next = i + 1 < n ? s[i + 1] : '\0';

            // Line comment: // or --
            if ((c == '/' && next == '/') || (c == '-' && next == '-'))
            {
                foreach (var t in FlushPlain()) yield return t;
                int start = i;
                while (i < n && s[i] != '\n') i++;
                yield return new Token(s.Substring(start, i - start), Comment, false, true);
                continue;
            }
            // Block comment: /* ... */
            if (c == '/' && next == '*')
            {
                foreach (var t in FlushPlain()) yield return t;
                int start = i; i += 2;
                while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/')) i++;
                i = Math.Min(i + 2, n);
                yield return new Token(s.Substring(start, i - start), Comment, false, true);
                continue;
            }
            // String literal "..."  ("" is an escaped quote)
            if (c == '"')
            {
                foreach (var t in FlushPlain()) yield return t;
                int start = i; i++;
                while (i < n)
                {
                    if (s[i] == '"') { if (i + 1 < n && s[i + 1] == '"') { i += 2; continue; } i++; break; }
                    i++;
                }
                yield return new Token(s.Substring(start, i - start), StringLit, false, false);
                continue;
            }
            // Quoted table name '...'  ('' is an escaped quote)
            if (c == '\'')
            {
                foreach (var t in FlushPlain()) yield return t;
                int start = i; i++;
                while (i < n)
                {
                    if (s[i] == '\'') { if (i + 1 < n && s[i + 1] == '\'') { i += 2; continue; } i++; break; }
                    i++;
                }
                yield return new Token(s.Substring(start, i - start), Reference, false, false);
                continue;
            }
            // Bracketed reference [Column] / [Measure]
            if (c == '[')
            {
                foreach (var t in FlushPlain()) yield return t;
                int start = i; i++;
                while (i < n && s[i] != ']') i++;
                i = Math.Min(i + 1, n);
                yield return new Token(s.Substring(start, i - start), Reference, false, false);
                continue;
            }
            // Number
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
            {
                foreach (var t in FlushPlain()) yield return t;
                int start = i;
                while (i < n && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                 ((s[i] == '+' || s[i] == '-') && i > start && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
                    i++;
                yield return new Token(s.Substring(start, i - start), Number, false, false);
                continue;
            }
            // Identifier -> function (followed by '(') or keyword, else plain text
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                string ident = s.Substring(start, i - start);

                int j = i;
                while (j < n && char.IsWhiteSpace(s[j])) j++;
                bool isFunction = j < n && s[j] == '(';

                if (isFunction)
                {
                    foreach (var t in FlushPlain()) yield return t;
                    yield return new Token(ident, Function, false, false);
                }
                else if (Keywords.Contains(ident))
                {
                    foreach (var t in FlushPlain()) yield return t;
                    yield return new Token(ident, Keyword, true, false);
                }
                else
                {
                    plain.Append(ident); // table names, etc. — neutral
                }
                continue;
            }

            plain.Append(c);
            i++;
        }

        foreach (var t in FlushPlain()) yield return t;
    }

    private static FlowDocument NewDoc() => new()
    {
        PagePadding = new Thickness(4),
        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
        FontSize = 12.5
    };

    private static Brush Hex(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
