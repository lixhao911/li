using System;
using System.Diagnostics;
using System.IO;

namespace PrinterUpperComputer.Services
{
    /// <summary>
    /// 外部开源切片引擎调用（可选模块）。
    ///
    /// 支持两种最常见的开源引擎命令行：
    ///
    /// 1) CuraEngine（Cura 的后台切片内核，开源 AGPL）：
    ///    CuraEngine slice -j 配置.json -l 模型.stl -o 输出.gcode
    ///    配置 json 可在 Cura 中：菜单 帮助 → 显示配置文件夹，或安装插件
    ///    “Settings Guide”/导出 profile；也可直接用 Cura 安装目录 share/cura/resources
    ///    下的 definitions 配合 -s 参数逐项覆盖。
    ///
    /// 2) PrusaSlicer（开源 AGPL，命令行最省事，推荐先用它跑通）：
    ///    prusa-slicer --slice --load 配置.ini --output 输出.gcode 模型.stl
    ///    配置 ini：PrusaSlicer 菜单 文件 → 导出 → 导出配置(Export Config)。
    ///
    /// 嫌命令行麻烦的话：直接在 Cura/PrusaSlicer 图形界面切片后“导出 G 代码”，
    /// 再用主界面“导入外部 G 代码”按钮装进来，效果完全一样。
    /// </summary>
    public static class ExternalSlicer
    {
        /// <summary>
        /// 调用外部引擎切片，成功后返回输出文件路径。
        /// </summary>
        /// <param name="enginePath">CuraEngine.exe 或 prusa-slicer.exe 完整路径</param>
        /// <param name="configPath">Cura 的 .json 或 Prusa 的 .ini 配置文件</param>
        /// <param name="modelPath">待切片的 STL/OBJ 完整路径</param>
        /// <param name="outputPath">输出 gcode 路径</param>
        /// <param name="timeoutMs">超时（毫秒），防止卡死</param>
        public static string Slice(string enginePath, string configPath,
                                   string modelPath, string outputPath,
                                   int timeoutMs = 120000)
        {
            if (!File.Exists(enginePath)) throw new FileNotFoundException("找不到切片引擎：" + enginePath);
            if (!File.Exists(modelPath)) throw new FileNotFoundException("找不到模型：" + modelPath);
            if (!File.Exists(configPath)) throw new FileNotFoundException("找不到配置文件：" + configPath);

            string engineName = Path.GetFileName(enginePath).ToLowerInvariant();
            string arguments;

            if (engineName.Contains("prusa"))
            {
                // PrusaSlicer CLI
                arguments = string.Format("--slice --load \"{0}\" --output \"{1}\" \"{2}\"",
                    configPath, outputPath, modelPath);
            }
            else
            {
                // 默认按 CuraEngine CLI
                arguments = string.Format("slice -j \"{0}\" -l \"{1}\" -o \"{2}\"",
                    configPath, modelPath, outputPath);
            }

            var psi = new ProcessStartInfo
            {
                FileName = enginePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var proc = new Process { StartInfo = psi })
            {
                proc.Start();

                // 读取输出（防止子进程缓冲区写满后阻塞）
                string stderr = proc.StandardError.ReadToEnd();
                string stdout = proc.StandardOutput.ReadToEnd();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    throw new TimeoutException("切片引擎执行超时。");
                }

                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "切片引擎返回错误码 " + proc.ExitCode +
                        "\r\n" + stderr + "\r\n" + stdout);
                }
            }

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("引擎执行完但未生成输出文件：" + outputPath);

            return outputPath;
        }
    }
}
