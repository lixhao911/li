using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PrinterUpperComputer.Model;
using PrinterUpperComputer.Services;

namespace PrinterUpperComputer
{
    /// <summary>
    /// 主窗体（所有控件均在代码里手工创建，不依赖 Form1.Designer.cs，
    /// 把本文件复制进 VS 就能直接编译运行）。
    ///
    /// 标准操作链路：
    ///   ① 拖入 STL/OBJ
    ///   ② 得到 G 代码：内置切片器 / 导入 Cura、PrusaSlicer 的 gcode / 直接调外部引擎
    ///   ③ 按“点距”把 G 代码转换为【点到点】点列（步进无插补，必须做这一步）
    ///   ④ 连接 PLC，逐点发送（PLC 轴到位 Done → Ack → 下一点）
    /// </summary>
    public class MainForm : Form
    {
        // ---------------- 业务对象 ----------------
        private Mesh _mesh;                              // 当前加载的 3D 模型
        private string _modelPath;                       // 模型完整路径（外部引擎切片要用）
        private readonly PlcService _plc = new PlcService();
        private readonly BindingList<GCodeCommand> _rows = new BindingList<GCodeCommand>();
        private readonly BindingSource _bs = new BindingSource();
        private CancellationTokenSource _sendCts;        // 发送任务取消令牌
        private bool _sending;                           // 是否正在逐点发送

        // ---------------- 通信时序常量 ----------------
        private const int AckTimeoutMs = 60000;           // 等 PLC Ack 的超时（点到点运动可能较慢）
        private const int AckPollIntervalMs = 25;         // 轮询 Ack 的间隔

        // ---------------- 控件：模型区 ----------------
        private Panel _pnlModel;
        private Label _lblDrop;
        private NumericUpDown _numLayer, _numInfill;
        private NumericUpDown _numMotionDist, _numTravelDist;
        private Label _lblModelInfo;
        private Button _btnSlice, _btnImportGCode, _btnExternal, _btnConvert;

        // ---------------- 控件：PLC 区 ----------------
        private Panel _pnlPlc;
        private TextBox _txtIp;
        private NumericUpDown _numRack, _numSlot, _numDelay, _numDb;
        private Button _btnConnect, _btnRead, _btnWriteOne, _btnSend, _btnStop;
        private Label _lblConn, _lblSendInfo, _lblP2pInfo;
        private ProgressBar _progress;

        // ---------------- 控件：表格与底部 ----------------
        private DataGridView _dgv;
        private Button _btnImport, _btnExport;
        private Label _lblEmpty;                         // 表格空状态提示

        // ---------------- 视觉规范：配色 / 字体 / 间距 ----------------
        // 深色顶栏 + 浅灰画布 + 白色面板，全局只用一个强调色（品牌蓝），避免多色竞争。
        private static readonly Color C_Canvas = Color.FromArgb(245, 246, 248);    // 页面底色
        private static readonly Color C_Surface = Color.White;                      // 面板底色
        private static readonly Color C_Divider = Color.FromArgb(226, 232, 240);    // 分隔线 / 描边
        private static readonly Color C_Border = Color.FromArgb(203, 213, 225);     // 控件描边
        private static readonly Color C_Text = Color.FromArgb(15, 23, 42);          // 主文字
        private static readonly Color C_TextSub = Color.FromArgb(71, 85, 105);      // 次要文字
        private static readonly Color C_TextMuted = Color.FromArgb(100, 116, 139);  // 提示文字
        private static readonly Color C_Accent = Color.FromArgb(37, 99, 235);       // 品牌蓝（唯一强调色）
        private static readonly Color C_AccentHover = Color.FromArgb(29, 78, 216);
        private static readonly Color C_AccentDown = Color.FromArgb(30, 64, 175);
        private static readonly Color C_Header = Color.FromArgb(15, 23, 42);        // 顶栏
        private static readonly Color C_HeaderSub = Color.FromArgb(148, 163, 184);  // 顶栏次要文字
        private static readonly Color C_Ok = Color.FromArgb(52, 211, 153);
        private static readonly Color C_Err = Color.FromArgb(248, 113, 113);

        private static readonly Font F_Body = new Font("Microsoft YaHei UI", 9F);
        private static readonly Font F_Label = new Font("Microsoft YaHei UI", 9F);
        private static readonly Font F_Section = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold);
        private static readonly Font F_Title = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
        private static readonly Font F_Button = new Font("Microsoft YaHei UI", 9F);
        private static readonly Font F_ButtonStrong = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private static readonly Font F_Hint = new Font("Microsoft YaHei UI", 8.5F);

        // 版式基准：20 外边距、16 区块间距、64 顶栏，所有坐标都由这三条推出来。
        private const int LayoutMargin = 20;
        private const int LayoutGap = 16;
        private const int HeaderHeight = 64;
        private const int SectionHeight = 376;

        // ---------------- 版本信息 ----------------
        // 版本号的唯一维护点是 csproj 的 Version / AssemblyVersion，这里只读取显示。
        private static readonly string AppVersion =
            typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        private ToolTip _tipVersion;

        public MainForm()
        {
            Text = "3D 打印上位机 v" + AppVersion + " · G 代码生成与 PLC 下发";
            ClientSize = new Size(1220, 900);
            MinimumSize = new Size(1100, 780);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = C_Canvas;
            Font = F_Body;
            DoubleBuffered = true;

            BuildHeader();
            BuildModelGroup();
            BuildPlcGroup();
            BuildGridAndBottom();

            FormClosing += (s, e) => _plc.Dispose();
        }

        // ==================================================================
        //  顶栏：品牌 + 通信状态（整窗唯一深色区域，作为视觉锚点）
        // ==================================================================
        private void BuildHeader()
        {
            var header = new Panel
            {
                Bounds = new Rectangle(0, 0, ClientSize.Width, HeaderHeight),
                BackColor = C_Header,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(header);

            header.Controls.Add(new Label
            {
                Text = "3D 打印上位机",
                Font = F_Title,
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(24, 9),
                BackColor = Color.Transparent
            });

            header.Controls.Add(new Label
            {
                Text = "STL / OBJ  →  G 代码  →  点到点点列  →  Siemens S7-1212C",
                Font = F_Body,
                ForeColor = C_HeaderSub,
                AutoSize = true,
                Location = new Point(26, 37),
                BackColor = Color.Transparent
            });

            _lblConn = new Label
            {
                Text = "●  未连接",
                Font = new Font("Microsoft YaHei UI", 9.5F),
                ForeColor = C_Err,
                TextAlign = ContentAlignment.MiddleRight,
                Bounds = new Rectangle(ClientSize.Width - 24 - 280, 20, 280, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Color.Transparent
            };
            header.Controls.Add(_lblConn);

            // 版本号：标题栏与顶栏都显示，悬停可看版本说明
            var lblVersion = new Label
            {
                Text = "v" + AppVersion,
                Font = F_Hint,
                ForeColor = C_HeaderSub,
                TextAlign = ContentAlignment.MiddleRight,
                Bounds = new Rectangle(ClientSize.Width - 24 - 280, 42, 280, 16),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Color.Transparent
            };
            header.Controls.Add(lblVersion);

            _tipVersion = new ToolTip();
            _tipVersion.SetToolTip(lblVersion,
                "3D 打印上位机 v" + AppVersion + "\r\n" +
                "STL / OBJ 切片生成 G 代码 → 点到点离散化 → S7 通信逐点下发 S7-1212C\r\n" +
                "版本变更记录见项目根目录 CHANGELOG.md");
        }

        // ==================================================================
        //  视觉组件：区块容器 / 字段标签 / 数字框 / 两种按钮
        // ==================================================================
        /// <summary>白色区块：标题 + 细分隔线 + 1px 浅描边（不用 GroupBox 的粗边框）。</summary>
        private static Panel CreateSection(string title, int x, int y, int w, int h, AnchorStyles anchor)
        {
            var panel = new Panel
            {
                Bounds = new Rectangle(x, y, w, h),
                BackColor = C_Surface,
                Anchor = anchor
            };

            panel.Paint += (s, e) =>
            {
                using (var pen = new Pen(C_Divider))
                    e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            };

            panel.Controls.Add(new Label
            {
                Text = title,
                Font = F_Section,
                ForeColor = C_Text,
                AutoSize = true,
                Location = new Point(LayoutMargin, 15),
                BackColor = Color.Transparent
            });

            panel.Controls.Add(new Panel
            {
                Bounds = new Rectangle(LayoutMargin, 48, w - LayoutMargin * 2, 1),
                BackColor = C_Divider,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            });

            return panel;
        }

        private static Label FieldLabel(string text, int x, int y, int w)
        {
            return new Label
            {
                Text = text,
                Bounds = new Rectangle(x, y, w, 20),
                Font = F_Label,
                ForeColor = C_TextSub,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            };
        }

        private static NumericUpDown CreateNumeric(int x, int y, int w,
                                                  decimal min, decimal max, decimal step,
                                                  decimal value, int decimals)
        {
            return new NumericUpDown
            {
                Bounds = new Rectangle(x, y, w, 26),
                Font = F_Body,
                ForeColor = C_Text,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Minimum = min,
                Maximum = max,
                Increment = step,
                Value = value,
                DecimalPlaces = decimals,
                TextAlign = HorizontalAlignment.Right
            };
        }

        /// <summary>主按钮：实心品牌蓝，每个区块最多一个。</summary>
        private static Button PrimaryButton(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Text = text,
                Bounds = new Rectangle(x, y, w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = C_Accent,
                ForeColor = Color.White,
                Font = F_ButtonStrong,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = C_AccentHover;
            b.FlatAppearance.MouseDownBackColor = C_AccentDown;
            return b;
        }

        /// <summary>次按钮：白底细描边，用于并列的辅助动作。</summary>
        private static Button SecondaryButton(string text, int x, int y, int w, int h)
        {
            var b = new Button
            {
                Text = text,
                Bounds = new Rectangle(x, y, w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = C_TextSub,
                Font = F_Button,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = C_Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 245, 249);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 232, 240);
            return b;
        }

        // ==================================================================
        //  ① 模型与切片
        // ==================================================================
        private void BuildModelGroup()
        {
            _pnlModel = CreateSection("模型与切片", LayoutMargin, HeaderHeight + LayoutGap,
                                      380, SectionHeight,
                                      AnchorStyles.Top | AnchorStyles.Left);
            Controls.Add(_pnlModel);

            // 拖放区：本质是一个 Label，支持 AllowDrop，自绘虚线框表示“可投入”
            _lblDrop = new Label
            {
                Text = "拖入 STL / OBJ 模型\r\n或点击浏览选择文件",
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = C_TextSub,
                Font = F_Body,
                Bounds = new Rectangle(LayoutMargin, 64, 340, 74),
                AllowDrop = true,
                Cursor = Cursors.Hand
            };
            _lblDrop.Paint += (s, e) =>
            {
                using (var pen = new Pen(C_Border) { DashStyle = DashStyle.Dash })
                    e.Graphics.DrawRectangle(pen, 0, 0, _lblDrop.Width - 1, _lblDrop.Height - 1);
            };
            _lblDrop.DragEnter += LblDrop_DragEnter;
            _lblDrop.DragDrop += LblDrop_DragDrop;
            _lblDrop.Click += (s, e) => BrowseAndLoadModel();
            _pnlModel.Controls.Add(_lblDrop);

            _lblModelInfo = new Label
            {
                Text = "尚未加载模型",
                Bounds = new Rectangle(LayoutMargin, 144, 340, 30),
                Font = F_Body,
                ForeColor = C_TextMuted,
                BackColor = Color.Transparent
            };
            _pnlModel.Controls.Add(_lblModelInfo);

            // 切片参数：两列，标签在左、数字框在右，列宽统一（单位统一为 mm，写在标签里）
            _pnlModel.Controls.Add(FieldLabel("层厚 mm", LayoutMargin, 180, 66));
            _numLayer = CreateNumeric(90, 178, 84, 0.05m, 1m, 0.05m, 0.2m, 2);
            _pnlModel.Controls.Add(_numLayer);

            _pnlModel.Controls.Add(FieldLabel("填充间距", 182, 180, 66));
            _numInfill = CreateNumeric(252, 178, 84, 0.2m, 5m, 0.1m, 1.0m, 2);
            _pnlModel.Controls.Add(_numInfill);

            _pnlModel.Controls.Add(FieldLabel("打印点距", LayoutMargin, 214, 66));
            _numMotionDist = CreateNumeric(90, 212, 84, 0.05m, 20m, 0.05m, 0.5m, 2);
            _pnlModel.Controls.Add(_numMotionDist);

            _pnlModel.Controls.Add(FieldLabel("空走点距", 182, 214, 66));
            _numTravelDist = CreateNumeric(252, 212, 84, 0m, 50m, 0.5m, 5m, 2);
            _pnlModel.Controls.Add(_numTravelDist);

            _btnSlice = SecondaryButton("② 生成 G 代码", LayoutMargin, 250, 340, 34);
            _btnSlice.Click += BtnSlice_Click;
            _pnlModel.Controls.Add(_btnSlice);

            _btnImportGCode = SecondaryButton("导入 G 代码", LayoutMargin, 292, 164, 30);
            _btnImportGCode.Click += BtnImportGCode_Click;
            _pnlModel.Controls.Add(_btnImportGCode);

            _btnExternal = SecondaryButton("外部引擎切片", 196, 292, 164, 30);
            _btnExternal.Click += BtnExternal_Click;
            _pnlModel.Controls.Add(_btnExternal);

            _btnConvert = PrimaryButton("③ 转换为点到点点列", LayoutMargin, 330, 340, 32);
            _btnConvert.Click += BtnConvert_Click;
            _pnlModel.Controls.Add(_btnConvert);
        }

        // ---------------- 拖放处理 ----------------
        private void LblDrop_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0 && IsModelFile(files[0]))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            }
        }

        private void LblDrop_DragDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0) LoadModelAsync(files[0]);
        }

        private void BrowseAndLoadModel()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "选择 3D 数模文件",
                Filter = "3D 模型 (*.stl;*.obj)|*.stl;*.obj|所有文件 (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    LoadModelAsync(dlg.FileName);
            }
        }

        private static bool IsModelFile(string path)
        {
            string ext = path.Substring(path.LastIndexOf('.')).ToLowerInvariant();
            return ext == ".stl" || ext == ".obj";
        }

        /// <summary>加载模型（解析放后台线程，避免大文件卡界面）。</summary>
        private async void LoadModelAsync(string path)
        {
            try
            {
                if (!IsModelFile(path))
                {
                    MessageBox.Show("只支持 STL / OBJ 文件。");
                    return;
                }

                _lblModelInfo.Text = "正在加载：" + System.IO.Path.GetFileName(path);
                _lblModelInfo.Refresh();

                Mesh mesh = await System.Threading.Tasks.Task.Run(
                    () => ModelLoader.Load(path));

                _mesh = mesh;
                _modelPath = path; // 外部引擎切片时要用完整路径
                _lblDrop.Text = "已加载：" + mesh.SourceFile +
                                "\r\n（可重新拖入或点击更换）";
                _lblModelInfo.Text = string.Format(
                    "三角面：{0} 个    外形尺寸：{1}",
                    mesh.Triangles.Count, mesh.SizeText);
            }
            catch (Exception ex)
            {
                ShowError("模型加载失败", ex);
                _lblModelInfo.Text = "加载失败";
            }
        }

        // ---------------- ② 内置切片 ----------------
        private async void BtnSlice_Click(object sender, EventArgs e)
        {
            if (_mesh == null)
            {
                MessageBox.Show("请先拖入或选择 STL / OBJ 模型。");
                return;
            }

            _btnSlice.Enabled = false;
            _btnSlice.Text = "切片中...";
            try
            {
                var settings = new SlicerSettings
                {
                    LayerHeight = (double)_numLayer.Value,
                    InfillSpacing = (double)_numInfill.Value
                };

                List<GCodeCommand> codes = await System.Threading.Tasks.Task.Run(
                    () => Slicer.Slice(_mesh, settings));

                ReplaceRows(codes);

                int motionCount = codes.Count(c => c.IsMotion);
                _lblSendInfo.Text = string.Format(
                    "内置切片完成：共 {0} 行，其中运动段 {1} 个。请执行③离散化。",
                    codes.Count, motionCount);
            }
            catch (Exception ex)
            {
                ShowError("切片失败", ex);
            }
            finally
            {
                _btnSlice.Enabled = true;
                _btnSlice.Text = "② 内置切片生成 G 代码";
            }
        }

        // ---------------- ② 导入外部 G 代码文件 ----------------
        private async void BtnImportGCode_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "导入切片软件/引擎导出的 G 代码",
                Filter = "G 代码 (*.gcode;*.nc;*.txt)|*.gcode;*.nc;*.txt|所有文件 (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    List<GCodeCommand> codes = await System.Threading.Tasks.Task.Run(
                        () => GCodeText.Load(dlg.FileName));

                    ReplaceRows(codes);

                    int motionCount = codes.Count(c => c.IsMotion);
                    _lblSendInfo.Text = string.Format(
                        "已导入外部 G 代码：{0} 行，运动段 {1} 个。请执行③离散化。",
                        codes.Count, motionCount);
                }
                catch (Exception ex)
                {
                    ShowError("导入 G 代码失败", ex);
                }
            }
        }

        // ---------------- ② 外部引擎切片（CuraEngine / PrusaSlicer） ----------------
        private async void BtnExternal_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_modelPath))
            {
                MessageBox.Show("请先加载 STL / OBJ 模型，再调用外部引擎。");
                return;
            }

            try
            {
                // 1) 选引擎可执行文件
                string enginePath;
                using (var dlg = new OpenFileDialog
                {
                    Title = "选择切片引擎（CuraEngine.exe / prusa-slicer.exe）",
                    Filter = "切片引擎 (*.exe)|*.exe|所有文件 (*.*)|*.*"
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    enginePath = dlg.FileName;
                }

                // 2) 选配置文件（Cura: .json，Prusa: .ini）
                string configPath;
                using (var dlg = new OpenFileDialog
                {
                    Title = "选择切片配置（Cura 的 .json / Prusa 的 .ini）",
                    Filter = "配置文件 (*.json;*.ini)|*.json;*.ini|所有文件 (*.*)|*.*"
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    configPath = dlg.FileName;
                }

                // 3) 选输出 gcode 位置
                string outputPath;
                using (var dlg = new SaveFileDialog
                {
                    Title = "选择输出 G 代码位置",
                    Filter = "G 代码 (*.gcode)|*.gcode",
                    FileName = System.IO.Path.GetFileNameWithoutExtension(_modelPath) + ".gcode"
                })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    outputPath = dlg.FileName;
                }

                _btnExternal.Enabled = false;
                _btnExternal.Text = "切片中...";

                await System.Threading.Tasks.Task.Run(() =>
                    ExternalSlicer.Slice(enginePath, configPath,
                                         _modelPath, outputPath));

                // 切片成功后直接装进来
                List<GCodeCommand> codes = await System.Threading.Tasks.Task.Run(
                    () => GCodeText.Load(outputPath));
                ReplaceRows(codes);

                _lblSendInfo.Text = string.Format(
                    "外部引擎切片完成并已装入：{0} 行，运动段 {1} 个。请执行③离散化。",
                    codes.Count, codes.Count(c => c.IsMotion));
            }
            catch (Exception ex)
            {
                ShowError("外部引擎切片失败", ex);
            }
            finally
            {
                _btnExternal.Enabled = true;
                _btnExternal.Text = "外部引擎切片";
            }
        }

        // ---------------- ③ 点到点离散化（核心） ----------------
        private async void BtnConvert_Click(object sender, EventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("请先通过步骤②得到 G 代码。");
                return;
            }

            _btnConvert.Enabled = false;
            _btnConvert.Text = "离散化中...";
            try
            {
                var settings = new PointDensifierSettings
                {
                    MotionPointDistance = (double)_numMotionDist.Value,
                    TravelPointDistance = (double)_numTravelDist.Value
                };

                List<GCodeCommand> snapshot = _rows.ToList();
                List<GCodeCommand> points = await System.Threading.Tasks.Task.Run(
                    () => PointDensifier.Convert(snapshot, settings));

                ReplaceRows(points);

                int printPoints = points.Count(p => p.MotionType == 1);
                int travelPoints = points.Count(p => p.MotionType == 0);
                _lblSendInfo.Text = string.Format(
                    "已转换为点到点点列：共 {0} 个点（打印点 {1}，空走点 {2}）。可连接PLC发送。",
                    points.Count, printPoints, travelPoints);
            }
            catch (Exception ex)
            {
                ShowError("离散化失败", ex);
            }
            finally
            {
                _btnConvert.Enabled = true;
                _btnConvert.Text = "③ 转换为点到点点列";
            }
        }

        /// <summary>用新列表整体替换表格内容。</summary>
        private void ReplaceRows(List<GCodeCommand> list)
        {
            _rows.Clear();
            foreach (GCodeCommand c in list) _rows.Add(c);

            if (_lblEmpty != null) _lblEmpty.Visible = list.Count == 0;
        }

        // ==================================================================
        //  ④ PLC 通信
        // ==================================================================
        private void BuildPlcGroup()
        {
            // 右侧区块：宽度随窗口伸缩（窗口 1220 时宽 784）
            int left = LayoutMargin + 380 + LayoutGap;
            int width = ClientSize.Width - LayoutMargin - left;

            _pnlPlc = CreateSection("PLC 通信", left, HeaderHeight + LayoutGap, width, SectionHeight,
                                    AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Controls.Add(_pnlPlc);

            // ---- 连接参数：IP / 机架 / 插槽 / 连接 ----
            _pnlPlc.Controls.Add(FieldLabel("IP 地址", LayoutMargin, 66, 60));
            _txtIp = new TextBox
            {
                Text = "192.168.0.1",
                Bounds = new Rectangle(84, 64, 130, 26),
                Font = F_Body,
                ForeColor = C_Text,
                BorderStyle = BorderStyle.FixedSingle
            };
            _pnlPlc.Controls.Add(_txtIp);

            _pnlPlc.Controls.Add(FieldLabel("机架", 226, 66, 36));
            _numRack = CreateNumeric(264, 64, 52, 0, 10, 1, 0, 0);
            _pnlPlc.Controls.Add(_numRack);

            _pnlPlc.Controls.Add(FieldLabel("插槽", 326, 66, 36));
            _numSlot = CreateNumeric(364, 64, 52, 0, 10, 1, 1, 0); // S7-1200 slot=1
            _pnlPlc.Controls.Add(_numSlot);

            _btnConnect = SecondaryButton("连接 PLC", width - LayoutMargin - 120, 62, 120, 30);
            _btnConnect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnConnect.Click += BtnConnect_Click;
            _pnlPlc.Controls.Add(_btnConnect);

            // ---- 数据块：块号可自定义 ----
            _pnlPlc.Controls.Add(FieldLabel("DB 块号", LayoutMargin, 106, 66));
            _numDb = CreateNumeric(88, 104, 66, 1, 9999, 1, 1, 0); // 与 PLC 中建立的 DB 块号一致
            _pnlPlc.Controls.Add(_numDb);

            _pnlPlc.Controls.Add(new Label
            {
                Text = "对应 TIA Portal 中的数据块编号，需取消“优化的块访问”",
                Bounds = new Rectangle(166, 106, 420, 20),
                Font = F_Hint,
                ForeColor = C_TextMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            });

            // ---- 读写与节奏 ----
            _btnRead = SecondaryButton("读 PLC 当前坐标", LayoutMargin, 140, 148, 32);
            _btnRead.Click += BtnRead_Click;
            _pnlPlc.Controls.Add(_btnRead);

            _btnWriteOne = SecondaryButton("写入选中点（单点）", 176, 140, 148, 32);
            _btnWriteOne.Click += BtnWriteOne_Click;
            _pnlPlc.Controls.Add(_btnWriteOne);

            // 每点延时：贴右对齐，与左侧读写按钮形成一行两端布局
            int msX = width - LayoutMargin - 32;
            int numX = msX - 8 - 70;
            int lblX = numX - 8 - 62;

            var lblDelay = FieldLabel("每点延时", lblX, 146, 62);
            lblDelay.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _pnlPlc.Controls.Add(lblDelay);

            _numDelay = CreateNumeric(numX, 142, 70, 0, 10000, 50, 0, 0); // 节奏由 Ack 决定，默认不额外延时
            _numDelay.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _pnlPlc.Controls.Add(_numDelay);

            var lblMs = new Label
            {
                Text = "ms",
                Bounds = new Rectangle(msX, 146, 32, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = F_Label,
                ForeColor = C_TextMuted,
                BackColor = Color.Transparent
            };
            _pnlPlc.Controls.Add(lblMs);

            // ---- 发送控制：主按钮唯一 ----
            _btnSend = PrimaryButton("开始发送", LayoutMargin, 180, 132, 34);
            _btnSend.Click += BtnSend_Click;
            _pnlPlc.Controls.Add(_btnSend);

            _btnStop = SecondaryButton("停止", 160, 180, 96, 34);
            _btnStop.Enabled = false;
            _btnStop.Click += (s, e) => _sendCts?.Cancel();
            _pnlPlc.Controls.Add(_btnStop);

            _progress = new ProgressBar
            {
                Bounds = new Rectangle(276, 190, width - 40 - 276, 14),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Minimum = 0, Maximum = 100, Value = 0
            };
            _pnlPlc.Controls.Add(_progress);

            _lblSendInfo = new Label
            {
                Text = "就绪：① 加载模型 → ② 生成 G 代码 → ③ 转换为点列 → ④ 连接并发送",
                Bounds = new Rectangle(LayoutMargin, 226, width - LayoutMargin * 2, 54),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = F_Body,
                ForeColor = C_TextSub,
                BackColor = Color.Transparent
            };
            _pnlPlc.Controls.Add(_lblSendInfo);

            _lblP2pInfo = new Label
            {
                Text = "点到点模式：每个点 = 一次绝对定位（MC_MoveAbsolute），PLC 在轴到位后置 Ack；上位机超时 60 s。\r\n" +
                       "建议 PLC 侧做 2~5 点缓冲队列：轴 Done 立刻装载下一点，可显著提升运动连续性。",
                Bounds = new Rectangle(LayoutMargin, 288, width - LayoutMargin * 2, 72),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = F_Hint,
                ForeColor = C_TextMuted,
                BackColor = Color.FromArgb(248, 250, 252),
                Padding = new Padding(10, 8, 10, 8)
            };
            _pnlPlc.Controls.Add(_lblP2pInfo);
        }

        // ---------------- 连接 / 断开 ----------------
        private async void BtnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                _plc.DbNumber = (int)_numDb.Value;

                if (_plc.IsConnected)
                {
                    await _plc.DisconnectAsync();
                }
                else
                {
                    _btnConnect.Enabled = false;
                    _lblConn.Text = "●  正在连接...";
                    _lblConn.ForeColor = C_HeaderSub;

                    await _plc.ConnectAsync(
                        _txtIp.Text.Trim(),
                        (short)_numRack.Value,
                        (short)_numSlot.Value);
                }
            }
            catch (Exception ex)
            {
                ShowError("PLC 连接失败", ex);
            }
            finally
            {
                _btnConnect.Enabled = true;
                UpdateConnectionUi();
            }
        }

        private void UpdateConnectionUi()
        {
            if (_plc.IsConnected)
            {
                _btnConnect.Text = "断开连接";
                _lblConn.Text = "●  已连接  " + _txtIp.Text.Trim();
                _lblConn.ForeColor = C_Ok;
            }
            else
            {
                _btnConnect.Text = "连接 PLC";
                _lblConn.Text = "●  未连接";
                _lblConn.ForeColor = C_Err;
            }
        }

        // ---------------- 读 ----------------
        private async void BtnRead_Click(object sender, EventArgs e)
        {
            try
            {
                _plc.DbNumber = (int)_numDb.Value;
                PlcPoint p = await _plc.ReadPointAsync();
                _lblSendInfo.Text = string.Format(
                    "PLC 回读 → X={0:0.###}  Y={1:0.###}  Z={2:0.###}  E={3:0.#####}\r\n" +
                    "NewData={4}  Ack={5}  Complete={6}",
                    p.X, p.Y, p.Z, p.E, p.NewData, p.Ack, p.Complete);
            }
            catch (Exception ex)
            {
                ShowError("读取失败", ex);
            }
        }

        // ---------------- 单点写入 ----------------
        private async void BtnWriteOne_Click(object sender, EventArgs e)
        {
            try
            {
                var c = _dgv.CurrentRow?.DataBoundItem as GCodeCommand;
                if (c == null || !c.IsMotion)
                {
                    MessageBox.Show("请在表格中选中一条 G0/G1 点行。");
                    return;
                }

                // 离散化后的点都带完整坐标；若不是，缺省轴按 0
                _plc.DbNumber = (int)_numDb.Value;
                await _plc.WritePointAsync(
                    c.LineNo, c.MotionType,
                    c.X ?? 0, c.Y ?? 0, c.Z ?? 0, c.E ?? 0);

                _lblSendInfo.Text = string.Format(
                    "已写入选中点 #{0}（{1}），未置 NewData，PLC 不会动作。",
                    c.LineNo, c.Code);
            }
            catch (Exception ex)
            {
                ShowError("写入失败", ex);
            }
        }

        // ---------------- 逐点发送 ----------------
        private async void BtnSend_Click(object sender, EventArgs e)
        {
            if (_sending) return;

            if (!_plc.IsConnected)
            {
                MessageBox.Show("请先连接 PLC。");
                return;
            }
            if (_rows.Count == 0)
            {
                MessageBox.Show("没有可发送的点，请先完成步骤②③。");
                return;
            }

            _sending = true;
            _btnSend.Enabled = false;
            _btnStop.Enabled = true;
            _numDb.Enabled = false; // 发送期间锁定 DB 块号，避免中途切换
            _sendCts = new CancellationTokenSource();

            try
            {
                await SendAllAsync(_sendCts.Token);
            }
            catch (OperationCanceledException)
            {
                _lblSendInfo.Text = "已手动停止发送。";
            }
            catch (TimeoutException ex)
            {
                ShowError("发送超时（PLC 未在规定时间内 Ack）", ex);
            }
            catch (Exception ex)
            {
                ShowError("发送失败", ex);
            }
            finally
            {
                _sending = false;
                _btnSend.Enabled = true;
                _btnStop.Enabled = false;
                _numDb.Enabled = true;
            }
        }

        /// <summary>
        /// 逐点发送（NewData/Ack 握手）：
        ///   PC 写 X/Y/Z/E/点号/类型 → NewData=true
        ///   PLC：把点装入 MC_MoveAbsolute（或缓冲队列），轴 Done/入队后 Ack=true
        ///   PC：NewData=false → PLC：Ack=false → 下一点
        /// </summary>
        private async System.Threading.Tasks.Task SendAllAsync(CancellationToken ct)
        {
            // 采用界面上的 DB 块号，并在本次发送期间保持不变
            _plc.DbNumber = (int)_numDb.Value;
            string addrNewData = _plc.DbAddress("DBX0.0");
            string addrAck = _plc.DbAddress("DBX0.1");
            string addrComplete = _plc.DbAddress("DBX0.2");

            // 发送前清握手与完成标志
            await _plc.SetFlagAsync(addrNewData, false); // NewData
            await _plc.SetFlagAsync(addrComplete, false); // Complete

            int total = _rows.Count(c => c.IsMotion);
            _progress.Maximum = Math.Max(1, total);
            _progress.Value = 0;
            int sent = 0;

            // 模态坐标（离散化后的点列本身已完整，这里仅作兜底）
            double curX = 0, curY = 0, curZ = 0, curE = 0;

            List<GCodeCommand> snapshot = _rows.ToList();

            foreach (GCodeCommand c in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                HighlightRow(c);

                if (!c.IsMotion) continue;

                if (c.X.HasValue) curX = c.X.Value;
                if (c.Y.HasValue) curY = c.Y.Value;
                if (c.Z.HasValue) curZ = c.Z.Value;
                if (c.E.HasValue) curE = c.E.Value;

                // 1) 先写数据
                await _plc.WritePointAsync(c.LineNo, c.MotionType,
                                           curX, curY, curZ, curE);

                // 2) NewData=true
                await _plc.SetFlagAsync(addrNewData, true);

                // 3) 等 Ack=true（轴到位/入队；超时 60 秒，点到点运动可能较慢）
                if (!await WaitFlagAsync(addrAck, true, AckTimeoutMs, ct))
                    throw new TimeoutException(
                        "等待 PLC Ack=true(轴到位) 超时，点序号 " + c.LineNo);

                // 4) NewData=false
                await _plc.SetFlagAsync(addrNewData, false);

                // 5) 等 Ack=false
                if (!await WaitFlagAsync(addrAck, false, AckTimeoutMs, ct))
                    throw new TimeoutException(
                        "等待 PLC Ack=false 超时，点序号 " + c.LineNo);

                sent++;
                _progress.Value = sent;
                _lblSendInfo.Text = string.Format(
                    "正在发送：{0}/{1}（点 #{2}，{3}）",
                    sent, total, c.LineNo, c.MotionType == 0 ? "G0 空走" : "G1 打印");

                if (_numDelay.Value > 0)
                    await System.Threading.Tasks.Task.Delay((int)_numDelay.Value, ct);
            }

            await _plc.SetFlagAsync(addrComplete, true);
            _lblSendInfo.Text = string.Format(
                "全部发送完成，共 {0} 个点，已置 DB{1}.DBX0.2 Complete=1。", sent, _plc.DbNumber);
        }

        /// <summary>轮询布尔位，直到等于期望值或超时；可被取消。</summary>
        private async System.Threading.Tasks.Task<bool> WaitFlagAsync(
            string address, bool expect, int timeoutMs, CancellationToken ct)
        {
            int start = Environment.TickCount;
            while (Environment.TickCount - start < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                bool v = await _plc.GetFlagAsync(address);
                if (v == expect) return true;
                await System.Threading.Tasks.Task.Delay(AckPollIntervalMs, ct);
            }
            return false;
        }

        /// <summary>表格选中并滚动到指定点。</summary>
        private void HighlightRow(GCodeCommand c)
        {
            int idx = _bs.IndexOf(c);
            if (idx < 0) return;
            try
            {
                _dgv.ClearSelection();
                _dgv.Rows[idx].Selected = true;
                _dgv.CurrentCell = _dgv.Rows[idx].Cells[1];
                if (idx > 4) _dgv.FirstDisplayedScrollingRowIndex = idx - 4;
            }
            catch { }
        }

        // ==================================================================
        //  点列数据（占满剩余高度，表头右侧放 CSV 导入/导出）
        // ==================================================================
        private void BuildGridAndBottom()
        {
            int top = HeaderHeight + LayoutGap + SectionHeight + LayoutGap;
            int sectionWidth = ClientSize.Width - LayoutMargin * 2;
            int sectionHeight = ClientSize.Height - top - LayoutMargin;

            var pnlGrid = CreateSection("点列数据", LayoutMargin, top, sectionWidth, sectionHeight,
                                        AnchorStyles.Top | AnchorStyles.Bottom |
                                        AnchorStyles.Left | AnchorStyles.Right);
            Controls.Add(pnlGrid);

            _btnExport = SecondaryButton("导出 CSV", sectionWidth - LayoutMargin - 130, 14, 130, 30);
            _btnExport.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnExport.Click += BtnExport_Click;
            pnlGrid.Controls.Add(_btnExport);

            _btnImport = SecondaryButton("导入 CSV", sectionWidth - LayoutMargin - 130 - 12 - 130, 14, 130, 30);
            _btnImport.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnImport.Click += BtnImport_Click;
            pnlGrid.Controls.Add(_btnImport);

            pnlGrid.Controls.Add(new Label
            {
                Text = "CSV 列：LineNo, Code, X, Y, Z, E；以 Code 列为准，建议导出离散化后的点列",
                Bounds = new Rectangle(140, 20, 600, 20),
                Font = F_Hint,
                ForeColor = C_TextMuted,
                BackColor = Color.Transparent
            });

            _dgv = new DataGridView
            {
                Bounds = new Rectangle(LayoutMargin, 64,
                                       sectionWidth - LayoutMargin * 2,
                                       sectionHeight - 64 - LayoutMargin),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom |
                         AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                AllowUserToAddRows = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                RowHeadersVisible = false,
                AutoGenerateColumns = false,
                BorderStyle = BorderStyle.None,
                BackgroundColor = Color.White,
                EnableHeadersVisualStyles = false,
                GridColor = Color.FromArgb(241, 245, 249),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersHeight = 38,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            _dgv.RowTemplate.Height = 28;
            _dgv.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(248, 250, 252),
                ForeColor = C_TextSub,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                SelectionBackColor = Color.FromArgb(248, 250, 252),
                SelectionForeColor = C_TextSub,
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0)
            };
            _dgv.DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = F_Body,
                ForeColor = C_Text,
                SelectionBackColor = Color.FromArgb(239, 246, 255),
                SelectionForeColor = C_Text,
                Padding = new Padding(10, 0, 0, 0)
            };
            _dgv.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(250, 251, 253)
            };

            AddTextColumn("LineNo", "点号", 8);
            AddTextColumn("Code", "点到点坐标（G 代码）", 34);
            AddTextColumn("X", "X", 12);
            AddTextColumn("Y", "Y", 12);
            AddTextColumn("Z", "Z", 12);
            AddTextColumn("E", "E", 14);

            _bs.DataSource = _rows;
            _dgv.DataSource = _bs;
            pnlGrid.Controls.Add(_dgv);

            // 空状态：没有数据时给出一句话引导，避免大片空白
            _lblEmpty = new Label
            {
                Text = "尚无点列数据 · 完成 ① 加载模型 → ② 生成 G 代码 → ③ 转换为点列后显示",
                Bounds = new Rectangle(LayoutMargin + 20, 64 + 96,
                                       sectionWidth - LayoutMargin * 2 - 40, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = F_Body,
                ForeColor = C_TextMuted,
                BackColor = Color.White
            };
            pnlGrid.Controls.Add(_lblEmpty);
            _lblEmpty.BringToFront();
        }

        private void AddTextColumn(string property, string header, float fillWeight)
        {
            _dgv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = property,
                HeaderText = header,
                FillWeight = fillWeight,
                MinimumWidth = 60,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private void BtnExport_Click(object sender, EventArgs e)
        {
            if (_rows.Count == 0)
            {
                MessageBox.Show("当前没有可导出的内容。");
                return;
            }

            using (var dlg = new SaveFileDialog
            {
                Title = "导出点列表格",
                Filter = "CSV 表格 (*.csv)|*.csv",
                FileName = "points.csv"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        GCodeCsv.Export(_rows.ToList(), dlg.FileName);
                        _lblSendInfo.Text = "已导出：" + dlg.FileName;
                    }
                    catch (Exception ex)
                    {
                        ShowError("导出失败", ex);
                    }
                }
            }
        }

        private void BtnImport_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "导入点列表格",
                Filter = "CSV 表格 (*.csv)|*.csv|所有文件 (*.*)|*.*"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    List<GCodeCommand> list = GCodeCsv.Import(dlg.FileName);
                    if (list.Count == 0)
                    {
                        MessageBox.Show("文件中没有解析到有效行。");
                        return;
                    }

                    ReplaceRows(list);
                    _lblSendInfo.Text = string.Format(
                        "已导入点列 {0} 个点。", list.Count);
                }
                catch (Exception ex)
                {
                    ShowError("导入失败", ex);
                }
            }
        }

        // ==================================================================
        //  通用错误弹框
        // ==================================================================
        private void ShowError(string title, Exception ex)
        {
            MessageBox.Show(this,
                ex.Message + "\r\n\r\n" + ex.GetType().Name,
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
