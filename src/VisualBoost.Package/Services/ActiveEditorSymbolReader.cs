using System;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace VisualBoost.Services;

internal static class ActiveEditorSymbolReader
{
    public static bool TryGetSymbol(DTE2 dte, out string symbol)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        symbol = string.Empty;
        try
        {
            if (dte.ActiveDocument?.Selection is not TextSelection selection)
            {
                return false;
            }

            var selectedText = selection.Text.Trim();
            if (IsIdentifier(selectedText))
            {
                symbol = selectedText;
                return true;
            }

            var activePoint = selection.ActivePoint;
            var lineStart = activePoint.CreateEditPoint();
            lineStart.StartOfLine();
            var lineEnd = lineStart.CreateEditPoint();
            lineEnd.EndOfLine();
            var line = lineStart.GetText(lineEnd);
            if (line.Length == 0)
            {
                return false;
            }

            var position = Math.Min(line.Length - 1, Math.Max(0, activePoint.LineCharOffset - 1));
            if (!IsIdentifierPart(line[position]) && position > 0 && IsIdentifierPart(line[position - 1]))
            {
                position--;
            }

            if (!IsIdentifierPart(line[position]))
            {
                return false;
            }

            var start = position;
            var end = position + 1;
            while (start > 0 && IsIdentifierPart(line[start - 1]))
            {
                start--;
            }

            while (end < line.Length && IsIdentifierPart(line[end]))
            {
                end++;
            }

            var candidate = line.Substring(start, end - start);
            if (!IsIdentifier(candidate))
            {
                return false;
            }

            symbol = candidate;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsIdentifierStart(value[0]))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsIdentifierPart(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
}
