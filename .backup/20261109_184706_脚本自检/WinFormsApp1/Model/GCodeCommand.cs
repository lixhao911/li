using System;
using System.Globalization;

namespace PrinterUpperComputer.Model
{
    /// <summary>
    /// 一行 G 代码。
    /// 界面表格、CSV 导入导出、向 PLC 下发，全部以它为数据单元。
    /// </summary>
    public class GCodeCommand
    {
        /// <summary>行号（从 1 开始）</summary>
        public int LineNo { get; set; }

        /// <summary>G 代码原文，例如 "G1 X10.000 Y20.000 E0.123 F1500"</summary>
        public string Code { get; set; } = "";

        /// <summary>
        /// 下面 X/Y/Z/E/F 为从原文中解析出的坐标参数，
        /// 用 double?（可空）表示“这一行没有写该轴”——G 代码是模态的，
        /// 没写就表示沿用上一次的值，下发 PLC 时由主程序负责补全。
        /// </summary>
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public double? E { get; set; }
        public double? F { get; set; }

        /// <summary>是否为运动指令（只有 G0/G1 才需要逐点下发给 PLC）</summary>
        public bool IsMotion { get; set; }

        /// <summary>运动类型：0 = G0 快速定位（不出料），1 = G1 直线插补（出料）</summary>
        public int MotionType { get; set; }

        /// <summary>
        /// 把一行原始 G 代码文本解析成 GCodeCommand。
        /// 切片器生成、CSV 导入，最终都走这个方法，保证数据口径一致。
        /// </summary>
        public static GCodeCommand Parse(int lineNo, string raw)
        {
            var cmd = new GCodeCommand
            {
                LineNo = lineNo,
                Code = raw ?? ""
            };

            string line = raw ?? "";

            // 1) 分号之后是注释，直接切掉
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line.Substring(0, semi);

            // 2) 按空白拆成 token，例如 "G1 X10 Y20 E0.1" → [G1, X10, Y20, E0.1]
            string[] tokens = line.Split(new[] { ' ', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return cmd; // 空行 / 纯注释行

            // 3) 第一个 token 是指令字
            string g = tokens[0].ToUpperInvariant();
            if (g == "G0" || g == "G00" || g == "G1" || g == "G01")
            {
                cmd.IsMotion = true;
                cmd.MotionType = (g == "G1" || g == "G01") ? 1 : 0;

                // 4) 逐个解析 X/Y/Z/E/F 参数（不区分大小写，数字按英制标点解析）
                for (int i = 1; i < tokens.Length; i++)
                {
                    string tok = tokens[i];
                    if (tok.Length < 2) continue;

                    if (!double.TryParse(tok.Substring(1),
                                         NumberStyles.Float,
                                         CultureInfo.InvariantCulture,
                                         out double v))
                    {
                        continue;
                    }

                    switch (char.ToUpperInvariant(tok[0]))
                    {
                        case 'X': cmd.X = v; break;
                        case 'Y': cmd.Y = v; break;
                        case 'Z': cmd.Z = v; break;
                        case 'E': cmd.E = v; break;
                        case 'F': cmd.F = v; break;
                    }
                }
            }

            return cmd;
        }
    }
}
