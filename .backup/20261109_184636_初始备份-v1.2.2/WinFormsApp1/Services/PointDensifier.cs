using System;
using System.Collections.Generic;
using System.Globalization;
using PrinterUpperComputer.Model;

namespace PrinterUpperComputer.Services
{
    /// <summary>
    /// 点到点离散化参数。
    /// </summary>
    public class PointDensifierSettings
    {
        /// <summary>
        /// 打印段(G1)点间距 mm：把连续直线拆成多长一个定位点。
        /// 这是点到点系统最重要的参数，详见 README“点距权衡”。
        /// </summary>
        public double MotionPointDistance = 0.5;

        /// <summary>
        /// 空走段(G0)点间距 mm。空走停一停无所谓、但为了少给 PLC 灌点可以取大些；
        /// 设为 0 表示不细分、直接走到目标。
        /// </summary>
        public double TravelPointDistance = 5.0;
    }

    /// <summary>
    /// 点到点离散化器（本程序的关键模块）。
    ///
    /// 为什么需要它：
    ///   CuraEngine / PrusaSlicer / 内置切片器输出的 G 代码里，一条 G1 可能是
    ///   几十上百毫米的长直线，意思是“沿直线插补走到目标”。但 S7-1200 的 PTO
    ///   步进控制只有 MC_MoveAbsolute / MC_MoveRelative，属于【点到点】：
    ///   给一个目标点，两轴各自走，没有直线插补。
    ///
    /// 做法：
    ///   把每条 G0/G1 长线段，按固定弧长点距 Δ 切成 N 小段，输出 N 个绝对坐标点。
    ///   段内点的坐标和 E 全部按弧长线性插值；于是：
    ///     - 斜线 = 一串小目标点，X/Y 同时给目标，近似走出直线；
    ///     - 每个点都是完整绝对坐标，PLC 逻辑最简单（MoveAbsolute + Done→Ack）；
    ///     - E 也变成逐点绝对目标，与 XY 在同一点位同时启动。
    ///
    /// 输出的每一行仍是 G0/G1 文本（再经 Parse 得到结构化坐标），
    /// 这样表格、CSV、发送链路完全复用，不需要改。
    /// </summary>
    public static class PointDensifier
    {
        public static List<GCodeCommand> Convert(
            List<GCodeCommand> source, PointDensifierSettings s)
        {
            var output = new List<GCodeCommand>();
            int lineNo = 0;

            // 模态当前坐标（G 代码缺省轴沿用上一次值），初始为 0
            double x = 0, y = 0, z = 0, e = 0;

            foreach (GCodeCommand c in source)
            {
                if (!c.IsMotion) continue; // 注释/G21/G90/G92 等不产生点位

                // 1) 模态补全，得到该段最终目标
                double tx = c.X ?? x;
                double ty = c.Y ?? y;
                double tz = c.Z ?? z;
                double te = c.E ?? e;

                // 2) 该段按运动类型选点距（G1 用打印点距，G0 用空走点距）
                double step = c.MotionType == 1
                    ? s.MotionPointDistance
                    : s.TravelPointDistance;

                // 3) 3D 弧长与细分点数（Z 抬升也计入弧长，虽然层间 Z 段很短）
                double len = Math.Sqrt(
                    (tx - x) * (tx - x) +
                    (ty - y) * (ty - y) +
                    (tz - z) * (tz - z));

                int n;
                if (step <= 0 || len <= 1e-9) n = 1;
                else n = Math.Max(1, (int)Math.Ceiling(len / step - 1e-9));

                // 4) 逐点插值输出（i 从 1 开始：段起点等于上一段终点，不重复输出）
                for (int i = 1; i <= n; i++)
                {
                    double t = i / (double)n;

                    double px = x + (tx - x) * t;
                    double py = y + (ty - y) * t;
                    double pz = z + (tz - z) * t;
                    double pe = e + (te - e) * t;

                    string raw;
                    if (c.MotionType == 1)
                    {
                        // 打印点：带 E，PLC 同时移动 XY(/Z) 并推进挤出轴
                        raw = string.Format(CultureInfo.InvariantCulture,
                            "G1 X{0:0.###} Y{1:0.###} Z{2:0.###} E{3:0.#####}",
                            px, py, pz, pe);
                    }
                    else
                    {
                        // 空走点：不带 E，PLC 不触发挤出轴
                        raw = string.Format(CultureInfo.InvariantCulture,
                            "G0 X{0:0.###} Y{1:0.###} Z{2:0.###}",
                            px, py, pz);
                    }

                    output.Add(GCodeCommand.Parse(++lineNo, raw));
                }

                // 5) 更新模态位置为本段终点
                x = tx; y = ty; z = tz; e = te;
            }

            return output;
        }
    }
}
