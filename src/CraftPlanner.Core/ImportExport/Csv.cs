using System.Text;

namespace CraftPlanner.Core.ImportExport;

/// <summary>RFC 4180 準拠の最小 CSV 読み書き。引用符内の改行・カンマ・二重引用符に対応する。</summary>
public static class Csv
{
    /// <summary>テキストを行×列に分解する。空行は無視する。</summary>
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1; // BOM

        void EndField()
        {
            fields.Add(sb.ToString());
            sb.Clear();
        }
        void EndRow()
        {
            EndField();
            if (!(fields.Count == 1 && fields[0].Length == 0)) rows.Add(fields.ToArray());
            fields.Clear();
        }

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case ',': EndField(); break;
                    case '\r':
                        if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                        EndRow();
                        break;
                    case '\n': EndRow(); break;
                    default: sb.Append(c); break;
                }
            }
        }
        if (sb.Length > 0 || fields.Count > 0) EndRow();
        return rows;
    }

    public static string Write(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(Escape)));
        }
        return sb.ToString();
    }

    public static string Escape(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    /// <summary>Excel で開けるよう BOM 付き UTF-8 で書く。</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
}
