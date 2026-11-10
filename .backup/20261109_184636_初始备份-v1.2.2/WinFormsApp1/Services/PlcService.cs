using System;
using System.Threading.Tasks;
using S7.Net;

namespace PrinterUpperComputer.Services
{
    /*
     * =====================================================================
     * 关于“Profinet / 通信协议”的说明（重要，避免概念混淆）：
     *
     * 西门子 S7-1212C 本体带的就是 Profinet 网口（RJ45）。PC 上位机与它
     * 通信在工程上普遍使用【S7 通信 / ISO-on-TCP（RFC1006，TCP 102 端口）】，
     * 开源库 S7netplus 实现的正是这一协议，PLC 侧只需勾一个“允许 PUT/GET”。
     *
     * 严格意义上的 Profinet IO 实时通道（RT/IRT）需要专用主站网卡和协议栈，
     * 用于“逐点下发 G 代码坐标”这类非硬实时场景没有必要，S7 通信即可。
     * =====================================================================
     *
     * 约定的 DB 寄存器映射（在 TIA Portal 中建立，必须【取消优化的块访问】）。
     * 块号默认 DB1，可通过 DbNumber 属性自定义（界面上的“DB 块号”）：
     *
     *   符号名        类型    绝对地址          含义
     *   NewData       Bool    %DBn.DBX0.0       PC→PLC：true 表示一组新坐标已就绪
     *   Ack           Bool    %DBn.DBX0.1       PLC→PC：true 表示 PLC 已取走该点
     *   Complete      Bool    %DBn.DBX0.2       PC→PLC：true 表示全部发送完毕
     *   X             Real    %DBn.DBD2         X 坐标 mm
     *   Y             Real    %DBn.DBD6         Y 坐标 mm
     *   Z             Real    %DBn.DBD10        Z 坐标 mm
     *   E             Real    %DBn.DBD14        挤出量
     *   LineNo        Int     %DBn.DBW18        G 代码行号
     *   MotionType    Int     %DBn.DBW20        0=G0 空走，1=G1 打印
     * =====================================================================
     */

    /// <summary>从 PLC 回读到的一组数据。</summary>
    public class PlcPoint
    {
        public float X, Y, Z, E;
        public bool NewData, Ack, Complete;
    }

    /// <summary>
    /// PLC 通信服务：对 S7netplus 的薄封装，全部做成异步（Task.Run），
    /// 界面线程不会因为网络读写而卡死。
    /// </summary>
    public class PlcService : IDisposable
    {
        private Plc _plc;

        /// <summary>目标 DB 块号（默认 1）。与 IP/机架/插槽无关，可在发送前随时修改。</summary>
        public int DbNumber { get; set; } = 1;

        /// <summary>把 DB 内部偏移拼成完整地址，例如 "DBX0.1" → "DB3.DBX0.1"。</summary>
        public string DbAddress(string suffix)
        {
            return "DB" + DbNumber + "." + suffix;
        }

        /// <summary>当前是否已连接（S7.Net 自带 IsConnected 属性）。</summary>
        public bool IsConnected
        {
            get { return _plc != null && _plc.IsConnected; }
        }

        /// <summary>连接超时（毫秒）。S7netplus 的 Open() 没有超时参数，这里自己计时。</summary>
        public const int ConnectTimeoutMs = 5000;

        /// <summary>
        /// 连接 S7-1200。
        /// S7-1200 的机架号 rack=0、插槽号 slot=1（S7-300 才是 rack0/slot2）。
        /// Open() 是同步调用且没有超时参数，因此放到独立任务里跑，
        /// 超过 ConnectTimeoutMs 仍未返回就判定超时，避免界面一直卡在“正在连接...”。
        /// </summary>
        public async Task ConnectAsync(string ip, short rack, short slot)
        {
            try { _plc?.Close(); } catch { /* 忽略旧连接关闭异常 */ }

            var plc = new Plc(CpuType.S71200, ip, rack, slot);
            Task openTask = Task.Run(() => plc.Open()); // 连不上会抛异常，由界面捕获后弹框

            Task winner = await Task.WhenAny(openTask, Task.Delay(ConnectTimeoutMs));
            if (winner != openTask)
            {
                // Open 还在后台重试：等它结束后关掉，避免遗留一个半连接
                _ = openTask.ContinueWith(t => { try { plc.Close(); } catch { } },
                                          TaskScheduler.Default);

                throw new TimeoutException(
                    "连接超时（" + ConnectTimeoutMs + " ms）：请检查 IP、网线、网段，" +
                    "以及 PLC 属性中是否勾选了“允许 PUT/GET”。");
            }

            await openTask; // Open 抛出的原始异常在这里还原，由界面捕获后弹框

            if (!plc.IsConnected)
                throw new InvalidOperationException("Open 已返回但连接未建立。");

            _plc = plc;
        }

        /// <summary>断开连接。</summary>
        public Task DisconnectAsync()
        {
            return Task.Run(() =>
            {
                if (_plc != null)
                {
                    _plc.Close();
                    _plc = null;
                }
            });
        }

        /// <summary>
        /// 写入一个坐标点（数据区）。Real 用 float，Int 用 short。
        /// 注意：调用顺序应是【先写数据，后置 NewData】，保证 PLC 看到标志时数据已稳定。
        /// </summary>
        public Task WritePointAsync(int lineNo, int motionType,
                                   double x, double y, double z, double e)
        {
            return Task.Run(() =>
            {
                EnsureOpen();
                _plc.Write(DbAddress("DBD2"), (float)x);
                _plc.Write(DbAddress("DBD6"), (float)y);
                _plc.Write(DbAddress("DBD10"), (float)z);
                _plc.Write(DbAddress("DBD14"), (float)e);
                _plc.Write(DbAddress("DBW18"), (short)lineNo);
                _plc.Write(DbAddress("DBW20"), (short)motionType);
            });
        }

        /// <summary>写一个布尔标志位（NewData / Ack / Complete 等）。</summary>
        public Task SetFlagAsync(string address, bool value)
        {
            return Task.Run(() =>
            {
                EnsureOpen();
                _plc.Write(address, value);
            });
        }

        /// <summary>读一个布尔标志位。</summary>
        public Task<bool> GetFlagAsync(string address)
        {
            return Task.Run(() =>
            {
                EnsureOpen();
                return (bool)_plc.Read(address);
            });
        }

        /// <summary>把当前 DB 块整组数据读回来（界面“读 PLC 当前坐标”按钮用）。</summary>
        public Task<PlcPoint> ReadPointAsync()
        {
            return Task.Run(() =>
            {
                EnsureOpen();
                return new PlcPoint
                {
                    X = (float)_plc.Read(DbAddress("DBD2")),
                    Y = (float)_plc.Read(DbAddress("DBD6")),
                    Z = (float)_plc.Read(DbAddress("DBD10")),
                    E = (float)_plc.Read(DbAddress("DBD14")),
                    NewData = (bool)_plc.Read(DbAddress("DBX0.0")),
                    Ack = (bool)_plc.Read(DbAddress("DBX0.1")),
                    Complete = (bool)_plc.Read(DbAddress("DBX0.2"))
                };
            });
        }

        /// <summary>通用读写：方便你以后扩展，直接给地址字符串即可。</summary>
        public Task WriteAsync(string address, object value)
        {
            return Task.Run(() => { EnsureOpen(); _plc.Write(address, value); });
        }

        public Task<object> ReadAsync(string address)
        {
            return Task.Run(() => { EnsureOpen(); return _plc.Read(address); });
        }

        private void EnsureOpen()
        {
            if (_plc == null || !_plc.IsConnected)
                throw new InvalidOperationException("PLC 尚未连接，请先点击【连接 PLC】。");
        }

        public void Dispose()
        {
            try { _plc?.Close(); } catch { }
            _plc = null;
        }
    }
}
