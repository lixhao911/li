# S7-1212C 通信配置与握手说明

本文档对应上位机 `PrinterUpperComputer` 的 PLC 侧配置。上位机通过 S7 通信（ISO-on-TCP，TCP 102 端口，S7netplus 实现）逐点下发坐标，PLC 侧需要在 TIA Portal 中做一次配置，并编写 NewData/Ack 握手程序。

## 1. 通信方式

- S7-1212C 的 Profinet 网口本身可承载 S7 通信，无需专用主站网卡；
- 上位机连接参数：CPU 类型 S7-1200、机架 rack=0、插槽 slot=1、端口 102；
- 上位机只做普通 S7 读写（PUT/GET），不做 Profinet IO 实时通道。

## 2. DB 变量定义

在 TIA Portal 中新建一个**全局 DB**（默认 DB1，上位机界面的“DB 块号”可改成其他编号，例如 DB3）。块内变量与偏移必须按下表建立：

| 符号名 | 数据类型 | 绝对地址（以 DB1 为例） | 方向 | 含义 |
| --- | --- | --- | --- | --- |
| NewData | Bool | %DB1.DBX0.0 | PC → PLC | true 表示一组新坐标已就绪 |
| Ack | Bool | %DB1.DBX0.1 | PLC → PC | true 表示 PLC 已取走该点（或已入队） |
| Complete | Bool | %DB1.DBX0.2 | PC → PLC | true 表示全部点发送完毕 |
| X | Real | %DB1.DBD2 | PC → PLC | X 坐标，mm |
| Y | Real | %DB1.DBD6 | PC → PLC | Y 坐标，mm |
| Z | Real | %DB1.DBD10 | PC → PLC | Z 坐标，mm |
| E | Real | %DB1.DBD14 | PC → PLC | 挤出轴累计位置，mm |
| LineNo | Int | %DB1.DBW18 | PC → PLC | 点序号（来自 G 代码行号） |
| MotionType | Int | %DB1.DBW20 | PC → PLC | 0 = G0 空走，1 = G1 打印 |

注意：偏移由前三个 Bool 之后的 Real 对齐决定（Real 从偶数字节 DBD2 开始），不要自行调整顺序或插入变量，否则上位机的绝对地址会对不上。

## 3. TIA Portal 操作步骤

1. **新建全局 DB**：项目树 → PLC → 程序块 → 添加新块 → 数据块（DB），选择“全局 DB”，编号与上位机“DB 块号”一致。
2. **建立变量**：按第 2 节的表逐行输入名称、数据类型；编译后可在 DB 的“偏移量”列核对上表地址。
3. **取消优化的块访问（关键）**：右键该 DB → 属性 → 常规 → 属性 → 取消勾选“优化的块访问”，确定后重新编译。不取消时变量没有固定绝对地址，上位机按 `DBD2` 等地址读写会失败或读到错误值。
4. **允许 PUT/GET（关键）**：右键 CPU → 属性 → 防护与安全 → 连接机制 → 勾选“允许来自远程对象的 PUT/GET 通信访问”。
5. **下载**：将硬件配置与程序下载到 PLC（下载后 CPU 需处于 RUN）。
6. **在线验证**：在线监视 DB，手动在上位机点“写入选中点(单点)”，观察 X/Y/Z/E/LineNo/MotionType 是否同步变化；再点“读 PLC 当前坐标”核对回读值。

## 4. 握手时序

上位机每个运动点的执行顺序：

1. PC 写 X/Y/Z/E/LineNo/MotionType；
2. PC 置 `NewData = true`；
3. PLC 取走该点（装入轴或缓冲队列）后置 `Ack = true`；
4. PC 置 `NewData = false`；
5. PLC 撤 `Ack = false`，PC 等 `Ack = false` 后发下一点；
6. 全部点发完，PC 置 `Complete = true`。

超时与节奏参数：

| 参数 | 值 | 位置 |
| --- | --- | --- |
| 连接超时 | 5 s | `PlcService.ConnectTimeoutMs` |
| 等 Ack 超时 | 60 s | `MainForm.AckTimeoutMs` |
| Ack 轮询间隔 | 25 ms | `MainForm.AckPollIntervalMs` |
| 每点附加延时 | 界面可调，默认 0 | `MainForm` 的“每点延时” |

## 5. SCL 示例

### 5.1 最小验证：不驱动轴，只验证读写与握手

放在 OB1（或循环中断 OB）中即可，用于先确认通信链路：

```scl
// 上位机置 NewData 后，PLC 立刻回 Ack，代表“已取走”
IF "GCodeDB".NewData AND NOT "GCodeDB".Ack THEN
    "GCodeDB".Ack := TRUE;
END_IF;

// 上位机撤销 NewData 后，PLC 撤 Ack，允许下一点
IF NOT "GCodeDB".NewData AND "GCodeDB".Ack THEN
    "GCodeDB".Ack := FALSE;
END_IF;
```

### 5.2 带缓冲队列的握手（推荐）

每点都等轴到位才 Ack，打印会一顿一顿；建议做 2~5 点环形缓冲：**NewData 只代表入队，立刻 Ack；轴 Done 时再装载下一点**。

另建全局 DB `PointQueue`（同样取消优化访问），结构如下：

| 名称 | 数据类型 |
| --- | --- |
| Point | Array[0..4] of Struct{ X, Y, Z, E : Real; LineNo, MotionType : Int } |
| Head | Int |
| Tail | Int |

入队部分（放进 OB1）：

```scl
// 1) 接收：NewData 置位即入队并立刻 Ack，不等轴动作
IF "GCodeDB".NewData AND NOT "GCodeDB".Ack THEN
    "PointQueue".Point["PointQueue".Tail].X := "GCodeDB".X;
    "PointQueue".Point["PointQueue".Tail].Y := "GCodeDB".Y;
    "PointQueue".Point["PointQueue".Tail].Z := "GCodeDB".Z;
    "PointQueue".Point["PointQueue".Tail].E := "GCodeDB".E;
    "PointQueue".Point["PointQueue".Tail].LineNo := "GCodeDB".LineNo;
    "PointQueue".Point["PointQueue".Tail].MotionType := "GCodeDB".MotionType;

    "PointQueue".Tail := ("PointQueue".Tail + 1) MOD 5;
    "GCodeDB".Ack := TRUE;
END_IF;

IF NOT "GCodeDB".NewData AND "GCodeDB".Ack THEN
    "GCodeDB".Ack := FALSE;
END_IF;
```

出队与四轴触发骨架（需先组态四个工艺对象 `Axis_X/Y/Z/E`，并各有 `MC_MoveAbsolute` 实例 DB）：

```scl
// 2) 缓冲非空且当前无运动时，同一个 Execute 上升沿触发四轴绝对定位
#HasPoint := ("PointQueue".Tail - "PointQueue".Head + 5) MOD 5 <> 0;

IF #HasPoint AND NOT #MoveBusy THEN
    #MoveBusy := TRUE;
    #Exec := TRUE;
END_IF;

// 各轴速度/加减速按行程比例配置，使四轴近似同时到达
"MC_MoveAbsolute_X_DB"(Axis := "Axis_X",
                       Execute := #Exec,
                       Position := "PointQueue".Point["PointQueue".Head].X,
                       Velocity := 50.0,
                       Acceleration := 200.0,
                       Deceleration := 200.0,
                       Done => #DoneX);

"MC_MoveAbsolute_Y_DB"(Axis := "Axis_Y",
                       Execute := #Exec,
                       Position := "PointQueue".Point["PointQueue".Head].Y,
                       Velocity := 50.0,
                       Acceleration := 200.0,
                       Deceleration := 200.0,
                       Done => #DoneY);

"MC_MoveAbsolute_Z_DB"(Axis := "Axis_Z",
                       Execute := #Exec,
                       Position := "PointQueue".Point["PointQueue".Head].Z,
                       Velocity := 50.0,
                       Acceleration := 200.0,
                       Deceleration := 200.0,
                       Done => #DoneZ);

// E 轴：仅 G1 打印点动作；G0 空走点保持当前位置（MotionType = 0 时不触发）
"MC_MoveAbsolute_E_DB"(Axis := "Axis_E",
                       Execute := #Exec AND "PointQueue".Point["PointQueue".Head].MotionType = 1,
                       Position := "PointQueue".Point["PointQueue".Head].E,
                       Velocity := 30.0,
                       Acceleration := 200.0,
                       Deceleration := 200.0,
                       Done => #DoneE);

// 3) 四轴全部 Done 后出队，并撤销 Execute（为下一点重新产生上升沿）
IF #DoneX AND #DoneY AND #DoneZ THEN
    #Exec := FALSE;
    #MoveBusy := FALSE;
    "PointQueue".Head := ("PointQueue".Head + 1) MOD 5;
END_IF;
```

要点：

- 四个轴共用同一个 `Execute`，点对点运动没有严格插补，只能靠按行程分配速度来近似同时到达；
- `Ack` 的时机是“参与轴全部 Done”，不要提前 Ack；
- `Execute` 必须在两点之间复位一次（产生下降沿），否则第二点不会触发；
- 缓冲深度 2~5 即可，太深会增加停止响应延迟。

## 6. 常见问题排查

| 现象 | 可能原因 | 处理 |
| --- | --- | --- |
| 连接超时（5 s） | IP 不通、网段不同、防火墙拦 102 端口、未允许 PUT/GET | 先 `ping` PLC；确认 PC 与 PLC 同网段；勾选允许 PUT/GET |
| 能连上但读写出错 / 数值乱 | DB 未取消“优化的块访问”，或界面 DB 块号与 PLC 不一致 | 取消优化访问并重新编译下载；核对“DB 块号” |
| 一直等 Ack 超时（60 s） | PLC 程序未置 Ack；Ack 时序写反；轴 Done 未接入 | 先跑 5.1 最小握手验证通信，再接轴 Done |
| 发送一顿一顿 | 每点都等轴到位才 Ack | 按 5.2 做 2~5 点缓冲队列 |
| 斜线变成明显折线 | 点距过大 | 减小“打印点距”（如 0.5 mm → 0.3 mm） |
| 电机抖动、失步、跑不到匀速 | 点距过小或速度过高 | 增大点距、降低进给；点距要明显大于起步到匀速所需距离 |
| 首点丢失 / 重复点 | NewData 未复位就发了下一点 | 确认 PLC 在你撤销 NewData 后才撤 Ack |
| 全部发完但 PLC 无动作 | `Complete` 未被 PLC 程序使用 | 按需在 PLC 中处理 `Complete`（如打印结束提示、归位） |

点距推荐起步值：打印点距 0.5 mm、空走点距 5 mm；打印速度先压到 F600~1200，再按失步情况调整加减速。
