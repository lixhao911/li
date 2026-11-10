using System;
using System.Windows.Forms;

namespace PrinterUpperComputer
{
    /// <summary>
    /// 程序入口。
    /// 整个程序的数据流（先有整体概念，后面每个文件都对应其中一环）：
    ///
    ///   3D 数模(STL/OBJ)
    ///        │  ① ModelLoader：解析三角网格（自写，支持二进制/ASCII STL、OBJ）
    ///        ▼
    ///     Mesh 三角面数据
    ///        │  ② Slicer：按层高水平面与三角面求交 → 轮廓环 → 扫描线填充
    ///        ▼
    ///     G 代码指令列表（G0/G1，含 X/Y/Z/E）
    ///        │  ③ 界面 DataGridView 显示；GCodeCsv 可导出/导入 CSV 表格
    ///        ▼
    ///   ④ PlcService（S7netplus）：逐点写入 DB1 寄存器，NewData/Ack 握手
    ///        ▼
    ///   西门子 S7-1212C（Profinet 口，IP 可在界面修改）
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // WinForms 标准初始化（传统写法，.NET Framework / .NET 8 都兼容）
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
