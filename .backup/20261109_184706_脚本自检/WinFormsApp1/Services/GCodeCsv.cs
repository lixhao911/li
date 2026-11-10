using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PrinterUpperComputer.Model;

namespace PrinterUpperComputer.Services
{
    /// <summary>
    /// G 代码 ↔ CSV 表格 互转。
    /// CSV 用 Excel 可直接打开（保存时选 UTF-8 或在 Excel 中“自文本/CSV”导入），
    /// 也可以人工在 Excel 里改完坐标再导回程序下发给 PLC。
    ///
    /// 表头固定为：LineNo,Code,X,Y,Z,E
    /// </summary>
    public static class GCodeCsv
    {
        /// <summary>导出 G 代码列表到 CSV 文件。</summary>
        public static void Export(IList<GCodeCommand> cmds, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("LineNo,Code,X,Y,Z,E");

            foreach (GCodeCommand c in cmds)
            {
                sb.Append(c.LineNo.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                AppendCsvField(sb, c.Code); // 原文可能含 ; , 等，必须加引号转义
                sb.Append(',');
                sb.Append(FormatNum(c.X));
                sb.Append(',');
                sb.Append(FormatNum(c.Y));
                sb.Append(',');
                sb.Append(FormatNum(c.Z));
                sb.Append(',');
                sb.Append(FormatNum(c.E));
                sb.AppendLine();
            }

            // 带 UTF-8 BOM，Excel 双击打开中文不乱码
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>从 CSV 文件导入，返回 G 代码列表（行号按文件顺序重新编排）。</summary>
        public static List<GCodeCommand> Import(string path)
        {
            var result = new List<GCodeCommand>();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            int lineNo = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;

                List<string> fields = ParseCsvLine(lines[i]);

                // 跳过表头
                if (i == 0 && fields.Count > 0 &&
                    fields[0].Trim().Equals("LineNo", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (fields.Count < 2) continue;

                // 第 2 列是 G 代码原文，以它为准重新解析
                string code = fields[1];
                if (string.IsNullOrWhiteSpace(code)) continue;

                result.Add(GCodeCommand.Parse(++lineNo, code));
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 下面是 CSV 字段的转义/解析（标准 RFC4180 简化实现）
        // ------------------------------------------------------------------

        /// <summary>字段含逗号、引号、换行时，用双引号包起来，内部引号写两遍。</summary>
        private static void AppendCsvField(StringBuilder sb, string field)
        {
            field = field ?? "";
            if (field.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            {
                sb.Append('"');
                sb.Append(field.Replace("\"", "\"\""));
                sb.Append('"');
            }
            else
            {
                sb.Append(field);
            }
        }

        /// <summary>解析一行 CSV，正确处理引号包裹与引号转义。</summary>
        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // 两个连续引号表示一个字面引号
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',')
                    {
                        fields.Add(sb.ToString());
                        sb.Clear();
                    }
                    else sb.Append(c);
                }
            }
            fields.Add(sb.ToString());
            return fields;
        }

        /// <summary>可空数字格式化：没有值就留空，有值保留 3 位小数。</summary>
        private static string FormatNum(double? v)
        {
            if (!v.HasValue) return "";
            return v.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
