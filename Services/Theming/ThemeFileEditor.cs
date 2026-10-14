using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// Changes single values in a theme file as text, so the comments, the order and the spacing of everything else stay
/// as the user wrote them. Each method returns the new text, or null when there is no safe place for the value.
/// </summary>
internal static class ThemeFileEditor
{
    private static readonly Regex ImageValue = new("(\"image\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled);
    private static readonly Regex StrengthValue = new("(\"strength\"\\s*:\\s*)-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.Compiled);
    private static readonly Regex PositionValue = new("(\"position\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled);
    private static readonly Regex BackgroundOpen = new("\"background\"\\s*:\\s*\\{", RegexOptions.Compiled);
    private static readonly Regex ColorsOpen = new("\"colors\"\\s*:\\s*\\{", RegexOptions.Compiled);
    private static readonly Regex ShapeOpen = new("\"shape\"\\s*:\\s*\\{", RegexOptions.Compiled);

    /// <summary>The background picture (a file name, or "none").</summary>
    public static string? SetImage(string text, string image)
    {
        string quoted = ThemeFileTemplate.Quote(image);
        return ReplaceInCode(text, ImageValue, m => m.Groups[1].Value + quoted)
            ?? InsertInBackground(text, " \"image\": " + quoted + ",");
    }

    /// <summary>How strongly the picture shows, 0 to 1.</summary>
    public static string? SetStrength(string text, double strength)
    {
        string value = Math.Round(Math.Clamp(strength, 0, 1), 2).ToString("0.##", CultureInfo.InvariantCulture);
        return ReplaceInCode(text, StrengthValue, m => m.Groups[1].Value + value)
            ?? InsertInBackground(text, " \"strength\": " + value + ",");
    }

    /// <summary>Which part of the picture stays in view: one of <see cref="BackgroundPosition.All"/>.</summary>
    public static string? SetPosition(string text, string position)
    {
        string quoted = ThemeFileTemplate.Quote(position);
        return ReplaceInCode(text, PositionValue, m => m.Groups[1].Value + quoted)
            ?? InsertInBackground(text, " \"position\": " + quoted + ",");
    }

    /// <summary>A top-level text setting such as "name" or "base".</summary>
    public static string? SetString(string text, string key, string value)
    {
        string quoted = ThemeFileTemplate.Quote(value);
        var existing = new Regex("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"");
        return ReplaceInCode(text, existing, m => m.Groups[1].Value + quoted)
            ?? InsertAfterFirstBrace(text, NewLine(text) + "  \"" + key + "\": " + quoted + ",");
    }

    /// <summary>
    /// One color of the theme. A line that sets it is changed; a line that was commented out (a color worked out from
    /// the others) is turned on with the new value; a color the file does not mention gets a line.
    /// </summary>
    public static string? SetColor(string text, string name, Color color)
    {
        string value = "\"" + ThemeColor.Format(color) + "\"";
        string key = "\"" + name + "\"";
        // The reader also takes a color written with its resource name ("BrushAccent"); that line is the one to change.
        string anyKey = "\"(?:Brush)?" + Regex.Escape(name) + "\"";

        var active = new Regex("(" + anyKey + "\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"");
        if (ReplaceInCode(text, active, m => m.Groups[1].Value + value) is { } changed) return changed;

        var commented = new Regex("^([ \\t]*)//[ \\t]*(" + anyKey + "[ \\t]*:[ \\t]*)\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Multiline);
        if (ReplaceInCode(text, commented, m => m.Groups[1].Value + m.Groups[2].Value + value) is { } turnedOn) return turnedOn;

        string line = NewLine(text) + "    " + key + ": " + value + ",";
        foreach (Match m in ColorsOpen.Matches(text))
        {
            if (!IsCode(text, m.Index)) continue;
            int at = m.Index + m.Length;
            return text[..at] + line + text[at..];
        }
        return InsertAfterFirstBrace(text, NewLine(text) + "  \"colors\": {" + line + NewLine(text) + "  },");
    }

    /// <summary>
    /// One corner radius of the theme, in the <c>shape</c> block: a line that sets it is changed; a block or a line that is missing is made.
    /// A radius is written as one number when all four corners are alike, and as four (top-left, top-right, bottom-right, bottom-left) when not.
    /// </summary>
    public static string? SetShape(string text, string key, CornerRadius radius)
    {
        string value = FormatRadius(radius);
        if (ShapeBlock(text) is { } block)
        {
            foreach (Match m in ShapeEntry(key).Matches(text))
            {
                if (m.Index < block.Start || m.Index >= block.End || !IsCode(text, m.Index)) continue;
                return text[..m.Index] + m.Groups[1].Value + value + text[(m.Index + m.Length)..];
            }
            string line = NewLine(text) + "    \"" + key + "\": " + value + ",";
            return text[..block.Start] + line + text[block.Start..];
        }
        return InsertAfterFirstBrace(text, NewLine(text) + "  \"shape\": {" + NewLine(text) + "    \"" + key + "\": " + value + "," + NewLine(text) + "  },");
    }

    /// <summary>Takes a radius out of the <c>shape</c> block, so the theme has the app's own again; the text is returned as it was when the block does not set it.</summary>
    public static string? RemoveShape(string text, string key)
    {
        if (ShapeBlock(text) is not { } block) return text;
        var alone = new Regex("^[ \\t]*\"(?:Radius)?" + Regex.Escape(key.StartsWith("Radius", StringComparison.Ordinal) ? key[6..] : key) + "\"\\s*:\\s*" + ShapeNumbers + "\\s*,?[ \\t]*(?://[^\\r\\n]*)?\\r?\\n", RegexOptions.Multiline);
        foreach (Match m in alone.Matches(text))
        {
            if (m.Index < block.Start || m.Index >= block.End || !IsCode(text, m.Index)) continue;
            return text[..m.Index] + text[(m.Index + m.Length)..];
        }
        var inline = new Regex("\"(?:Radius)?" + Regex.Escape(key.StartsWith("Radius", StringComparison.Ordinal) ? key[6..] : key) + "\"\\s*:\\s*" + ShapeNumbers + "\\s*,?");
        foreach (Match m in inline.Matches(text))
        {
            if (m.Index < block.Start || m.Index >= block.End || !IsCode(text, m.Index)) continue;
            return text[..m.Index] + text[(m.Index + m.Length)..];
        }
        return text;
    }

    private const string ShapeNumbers = "(?:\\[[^\\]]*\\]|-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)";

    private static Regex ShapeEntry(string key)
    {
        string bare = key.StartsWith("Radius", StringComparison.Ordinal) ? key[6..] : key;
        return new Regex("(\"(?:Radius)?" + Regex.Escape(bare) + "\"\\s*:\\s*)" + ShapeNumbers);
    }

    /// <summary>A radius as a theme file writes it: one number when the four corners are alike, otherwise four.</summary>
    internal static string FormatRadius(CornerRadius r)
    {
        static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        return r.TopLeft == r.TopRight && r.TopRight == r.BottomRight && r.BottomRight == r.BottomLeft
            ? N(r.TopLeft)
            : "[" + N(r.TopLeft) + ", " + N(r.TopRight) + ", " + N(r.BottomRight) + ", " + N(r.BottomLeft) + "]";
    }

    /// <summary>The inside of the first <c>"shape": { ... }</c> that is JSON: where it starts (just after the brace) and where its closing brace is.</summary>
    private static (int Start, int End)? ShapeBlock(string text)
    {
        foreach (Match m in ShapeOpen.Matches(text))
        {
            if (!IsCode(text, m.Index)) continue;
            int start = m.Index + m.Length;
            bool inString = false, lineComment = false, blockComment = false;
            int depth = 1;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (lineComment) { if (c == '\n') lineComment = false; }
                else if (blockComment) { if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { blockComment = false; i++; } }
                else if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; }
                else if (c == '"') inString = true;
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { lineComment = true; i++; }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { blockComment = true; i++; }
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return (start, i);
            }
            return null;
        }
        return null;
    }

    // ---- Finding the place ------------------------------------------------------------------------------------

    private static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>The text with the first match that is JSON (not inside a comment or a string) replaced; null when there is none.</summary>
    private static string? ReplaceInCode(string text, Regex regex, Func<Match, string> replacement)
    {
        foreach (Match m in regex.Matches(text))
        {
            if (!IsCode(text, m.Index)) continue;
            return text[..m.Index] + replacement(m) + text[(m.Index + m.Length)..];
        }
        return null;
    }

    /// <summary>Puts <paramref name="member"/> inside the background block, making the block when there is none.</summary>
    private static string? InsertInBackground(string text, string member)
    {
        foreach (Match m in BackgroundOpen.Matches(text))
        {
            if (!IsCode(text, m.Index)) continue;
            int at = m.Index + m.Length;
            return text[..at] + member + text[at..];
        }
        return InsertAfterFirstBrace(text, NewLine(text) + "  \"background\": {" + member.TrimEnd(',') + " },");
    }

    private static string? InsertAfterFirstBrace(string text, string insertion)
    {
        int open = FirstCodeBrace(text);
        return open < 0 ? null : text[..(open + 1)] + insertion + text[(open + 1)..];
    }

    /// <summary>True when the character at <paramref name="index"/> is JSON itself: not inside a string, a // comment or a /* comment */.</summary>
    private static bool IsCode(string text, int index)
    {
        bool inString = false, lineComment = false, blockComment = false;
        for (int i = 0; i < index; i++)
        {
            char c = text[i];
            if (lineComment) { if (c == '\n') lineComment = false; }
            else if (blockComment) { if (c == '*' && i + 1 < text.Length && text[i + 1] == '/') { blockComment = false; i++; } }
            else if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; }
            else if (c == '"') inString = true;
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { lineComment = true; i++; }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { blockComment = true; i++; }
        }
        return !inString && !lineComment && !blockComment;
    }

    private static int FirstCodeBrace(string text)
    {
        for (int i = text.IndexOf('{'); i >= 0; i = text.IndexOf('{', i + 1))
            if (IsCode(text, i)) return i;
        return -1;
    }
}
