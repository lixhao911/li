using System.Collections.Generic;
using System.IO;
using System.Text;
using PrinterUpperComputer.Model;

namespace PrinterUpperComputer.Services
{
    /// <summary>
    /// 标准 G 代码文本文件（.gcode / .nc / .txt）的读写。
    ///
    /// 典型用途：
    ///   Cura / PrusaSlicer 里点“导出 G 代码”，或 CuraEngine 命令行切片产出的
    ///   output.gcode，用本类装到程序里 → 再交给 PointDensifier 离散成点到点点列。
    /// </summary>
    public static class GCodeText
    {
        /// <summary>逐行读取 G 代码文件（注释行也保留，离散化时会自动跳过）。</summary>
        public static List<GCodeCommand> Load(string path)
        {
            var result = new List<GCodeCommand>();
            int lineNo = 0;

            // 切片软件导出的 gcode 多为 UTF-8；ReadAllLines 默认即 UTF-8
            foreach (string raw in File.ReadAllLines(path))
                result.Add(GCodeCommand.Parse(++lineNo, raw));

            return result;
        }

        /// <summary>把当前列表按原文写回 .gcode 文件（也可用于保存离散化后的点列）。</summary>
        public static void Save(IList<GCodeCommand> cmds, string path)
        {
            var sb = new StringBuilder();
            foreach (GCodeCommand c in cmds)
                sb.AppendLine(c.Code);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
