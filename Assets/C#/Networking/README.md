# 局域网联机模块（Unity3D 射击游戏 · 起步脚手架）

交换机局域网联机，**房主同时开 TCP + UDP 两个监听端口，端口号相同**，所以只需要把
一个地址（例如 `192.168.1.3:10003`）告诉队友即可。

- **TCP**：Client 首次接入、握手、房间大厅（可靠，不丢包）
- **UDP**：进入游戏后的实时同步（输入上行 / 世界快照下行，低延迟）

协议用 **protobuf proto3**，C# 代码由你自己用 `protoc` 生成。

---

## 一、目录结构

```
Assets/Scripts/Networking/
├── Protocol/
│   ├── game.proto                  ← 协议定义（proto3）
│   ├── generate_proto.bat          ← 一键调 protoc 生成 Game.cs
│   └── ProtoCodec.cs               ← TCP 长度前缀分帧 + UDP 包头封装 + protobuf 编解码
├── Core/
│   ├── NetConst.cs                 ← 端口 / 超时 / 包类型等常量
│   ├── NetUtil.cs                  ← 本机 IPv4 探测（跳过虚拟网卡）、地址解析、单调时钟
│   ├── NetLog.cs                   ← 日志开关
│   └── MainThreadDispatcher.cs     ← 后台线程 → Unity 主线程的回调派发
├── Host/
│   ├── TcpHostServer.cs            ← 房主 TCP 监听（accept + 每连接收发循环）
│   ├── UdpHostServer.cs            ← 房主 UDP 监听（收包 + 拆头 + 广播）
│   ├── TcpPeer.cs                  ← 单条 TCP 连接的服务端视图
│   ├── PlayerSession.cs            ← 玩家会话（TCP 连接 + UDP 端点 + 令牌 + 心跳）
│   ├── RoomState.cs                ← 房间玩家表（线程安全）
│   └── HostNetworkManager.cs       ← ★ 房主总入口：CreateRoom() 同时开两个端口
├── Client/
│   ├── TcpClientChannel.cs         ← 客机 TCP 通道
│   ├── UdpClientChannel.cs         ← 客机 UDP 通道（含握手重发 / 心跳 / 超时）
│   └── ClientNetworkManager.cs     ← ★ 客机总入口：JoinRoom("192.168.1.3:10003")
└── UI/
    └── MainMenuController.cs       ← ★ 主界面：「创建房间」「加入房间」按钮
```

`generate_proto.bat` 生成物和 `Game.cs`（命名空间 `Game.Protocol`）会落在 `Protocol/` 下。

---

## 二、接入步骤

### 1. 放 Google.Protobuf 运行时

从你装好的 NuGet 包里把 DLL 拖进 Unity：

```
<NuGet缓存>\Google.Protobuf\<版本>\lib\netstandard2.0\Google.Protobuf.dll
        ↓ 复制到
Assets/Plugins/Google.Protobuf.dll
```

> `protoc` 的版本 **不能比** 运行时 DLL 的版本新太多，否则生成的代码可能引用不到 API。
> 本模块用 `protoc 28.3` + `Google.Protobuf 3.32.0` 实测通过。

`Project Settings → Player → Api Compatibility Level` 设为 **.NET Standard 2.1**（默认值即可）。

### 2. 生成 Game.cs

在 `Assets/Scripts/Networking/Protocol/` 下双击 `generate_proto.bat`，或手动执行：

```cmd
cd /d "D:\C#ARMY\C#\Assets\Scripts\Networking\Protocol"
protoc --proto_path=. --csharp_out=. game.proto
```

生成 `Game.cs`（命名空间 `Game.Protocol`）。

### 3. 搭主界面

1. 新建空物体 `NetworkRoot`，挂上 **`MainMenuController`**。
2. 按需把 UI 控件拖到 Inspector 对应字段（**都可以留空**，留空只是少显示一块信息，不会报错）：

| 字段 | 作用 |
|---|---|
| `createRoomButton` | 「创建房间」按钮 |
| `joinRoomButton` | 「加入房间」按钮 |
| `playerNameInput` | 昵称输入框 |
| `addressInput` | 房主地址输入框，默认 `192.168.1.3:10003` |
| `roomTitleText` / `addressText` | 房间标题 / **本机地址显示** |
| `copyAddressButton` | 复制地址到剪贴板 |
| `playerListText` | 玩家列表 |
| `readyButton` / `startGameButton` | 准备 / 开始游戏 |
| `leaveRoomButton` | 离开房间 |
| `nicDropdown` | （可选）多网卡时手动选对外公布的网卡 |
| `statusText` | 状态提示 |
| `gameSceneName` | 开始游戏后自动加载的场景名，留空则由你的代码接管 |

> UI 用的是内置 UGUI（`InputField`/`Text`/`Button`），不依赖 TextMeshPro。
> 项目用 TMP 的话，把类型换成 `TMP_InputField` / `TMP_Text` 即可，逻辑不用动。

`HostNetworkManager` / `ClientNetworkManager` **不需要手动挂**，`MainMenuController`
会在点按钮时自动创建（常驻 `DontDestroyOnLoad`）。想自己挂也行，效果一样。

### 4. Windows 防火墙（局域网联机最常见的坑）

第一次运行时 Windows 会弹「是否允许访问网络」，**必须勾选「专用网络」并允许**。
如果没弹或点错了：`Windows 安全中心 → 防火墙和网络保护 → 允许应用通过防火墙`，
把游戏的 exe 加入并勾上「专用」。UDP 入站尤其容易被拦。

---

## 三、握手流程（时序）

```
客机                                          房主
 │                                             │  点击「创建房间」
 │                                             │  ├─ TCP 监听 0.0.0.0:10003
 │                                             │  ├─ UDP 监听 0.0.0.0:10003
 │                                             │  └─ 探测本机 IPv4 → 192.168.1.3
 │                                             │     显示 "192.168.1.3:10003"
 │                                             │
 │── TCP connect 192.168.1.3:10003 ───────────►│
 │── JoinRequest{name, version} ──────────────►│  分配 playerId、生成 sessionToken
 │◄─ JoinAccepted{playerId, token,            │
 │     udpPort=10003, players[]} ──────────────│
 │                                             │
 │── UDP Hello{playerId, token} ──────────────►│  校验令牌，把 UDP 端点绑定到会话
 │◄─ UDP HelloAck ─────────────────────────────│  （重发直到成功，超时 6s）
 │                                             │
 │══ 之后：UDP Input ─────────────────────────►│  20Hz 逻辑帧
 │◄─────────────── UDP Snapshot ───────────────│  广播世界快照
 │                                             │
 │◄─────────────── TCP Ping/Pong ─────────────►│  心跳 + RTT（2s）
```

**玩家 ID**：房主固定是 `1`，之后依次 `2, 3, 4…`（`0` 表示未分配，proto3 默认值安全）。

**安全**：每个玩家有独立 `sessionToken`，UDP 握手必须带对令牌，否则房主回 `UdpBye` 拒绝。
（防止别人猜到玩家 ID 就插进房间。局域网小游戏够用，公网方案需要另外加加密。）

---

## 四、把游戏逻辑接上去

### 房主：每个逻辑帧广播快照

```csharp
void OnEnable()
{
    HostNetworkManager.Instance.ServerTick += OnServerTick;   // 默认 20Hz
}

void OnServerTick(uint tick)
{
    var snap = new UdpSnapshot { ServerTick = tick, ServerTimeMs = (ulong)NetUtil.NowMs };
    foreach (var s in HostNetworkManager.Instance.Room.Snapshot())
    {
        snap.Players.Add(new PlayerState {
            PlayerId = s.PlayerId,
            PosX = ..., PosY = ..., PosZ = ...,
            Flags = alive ? 1u : 0u,
        });
    }
    HostNetworkManager.Instance.BroadcastUdp(UdpPacketType.Snapshot, snap);
}

// 处理玩家输入
void Start()
{
    HostNetworkManager.Instance.DatagramReceived += (session, type, body) =>
    {
        if (type != UdpPacketType.Input) return;
        UdpInput input;
        if (!ProtoCodec.TryParseUdpBody(body, 0, body.Length, out input)) return;
        // ... 把 input 应用到你自己的角色控制器上
    };
}
```

### 客机：上行输入 + 下行快照

```csharp
void Update()
{
    var net = ClientNetworkManager.Instance;
    if (net == null || !net.IsJoined) return;

    uint buttons = 0;
    if (Input.GetButton("Fire1")) buttons |= 1u;

    net.SendInput(Input.GetAxisRaw("Horizontal"),
                  Input.GetAxisRaw("Vertical"),
                  transform.eulerAngles.y,
                  cameraPitch,
                  buttons);
}

void Start()
{
    ClientNetworkManager.Instance.DatagramReceived += (type, body) =>
    {
        if (type != UdpPacketType.Snapshot) return;
        UdpSnapshot snap;
        if (!ProtoCodec.TryParseUdpBody(body, 0, body.Length, out snap)) return;
        // ... 插值 / 校正本地玩家位置 / 更新其他玩家
    };
}
```

### 常用接口速查

| 房主 `HostNetworkManager` | 说明 |
|---|---|
| `CreateRoom(out string error)` | 开房，成功后 `HostAddress` 就是 `192.168.1.3:10003` |
| `CloseRoom()` | 关房（延迟 0.25s，保证 Kick 包发得出去） |
| `SendTcp(id, msg)` / `BroadcastTcp(msg)` | 大厅类消息 |
| `SendUdp(id, type, msg)` / `BroadcastUdp(type, msg)` | 游戏同步 |
| `KickPlayer(id, reason)` | 踢人 |
| `StartGame()` | 广播 `StartGame` 并生成 `mapSeed` |
| `Room.Snapshot()` | 当前玩家列表 |
| 事件 | `RoomStarted` `RoomStopped` `PlayerJoined` `PlayerLeft` `PlayerUdpReady` `EnvelopeReceived` `DatagramReceived` `ServerTick` |

| 客机 `ClientNetworkManager` | 说明 |
|---|---|
| `JoinRoom("192.168.1.3:10003", "昵称")` | 加入 |
| `LeaveRoom()` | 离开 |
| `IsJoined` / `IsUdpReady` / `PlayerId` / `UdpRttMs` | 状态 |
| `SendInput(...)` / `SendTcp(msg)` / `SetReady(bool)` / `SendChat(text)` | 发送 |
| 事件 | `Joined` `JoinFailed` `Disconnected` `UdpLost` `RemotePlayerJoined` `RemotePlayerLeft` `GameStarting` `DatagramReceived` |

**所有事件都在 Unity 主线程触发**，可以直接碰 `Transform`、UI。

---

## 五、几个刻意的设计选择

| 选择 | 原因 |
|---|---|
| TCP/UDP 同端口号 | 队友只需要记一个地址，不用分别填两个端口 |
| 端口占用自动 +1 重试（最多 10 次） | 宿舍/网吧一台机器开多个客户端时不会互相抢端口 |
| 本机 IPv4 探测会跳过 VM/Hyper-V/VPN/WSL 等虚拟网卡，优先「有网关 + 内网段 + 物理网卡」 | 否则很容易把 `192.168.56.1`（VirtualBox）显示给队友，谁连谁超时 |
| TCP 用 4 字节大端长度前缀 | protobuf 本身不带边界，字节流必须自己分帧；大端是网络序，日志里也好读 |
| UDP 加 4 字节头（魔数 + 类型 + 版本） | 交换机上广播包很多，靠魔数直接丢弃杂包；版本号方便以后协议升级 |
| UDP 负载上限 1200 字节 | 避免 IP 分片（MTU 1500 − IP/UDP 头），分片丢一个就整包废掉 |
| 发送走队列 + 单协程 | 多线程直接写同一个 `NetworkStream` 会把帧交错写坏 |
| 后台线程 → 主线程派发队列 | Unity API 只能在主线程调用 |
| 心跳 2s / 超时 10s | 拔网线后 10s 内其他人就能看到你掉线 |

---

## 六、已验证内容

`protoc 28.3` 生成 + `dotnet build`（`netstandard2.1`）零错误零警告，
并用真实 socket 在回环上跑通了 **66 项端到端断言**，包括：

- 工具函数：内网地址判定、`192.168.1.3` / `192.168.1.3:10003` / `tcp://10.0.0.9:20000/` 解析
- 本机 IPv4 探测（实测正确跳过了 Hyper-V 的 `26.x` 和 `169.254.x` 网卡）
- UDP 魔数 / 版本校验，杂包被丢弃
- 房主 **TCP 与 UDP 同端口**（10003）监听成功
- 客机 TCP 接入 → `JoinRequest` → `JoinAccepted`（含玩家 ID、令牌、UDP 端口）
- 客机 UDP 握手 `Hello → HelloAck` 完成
- 客机上行 `UdpInput` → 房主收到，`float` 数值无损、按键位掩码无损
- 房主下行 `UdpSnapshot` → 客机收到，位置数值无损
- TCP 分帧：30KB 单帧消息完整送达
- **伪造 `sessionToken` 的 UDP 握手被房主拒绝**
- `KickPlayer` → 客机收到断开通知且原因正确透传
- `CloseRoom()` → 端口立刻释放，可以马上重新开房
- 起始端口被占用时自动回退到 `+1`，且 TCP/UDP 仍然同号

回归测试（可选，Unity 工程会忽略 `.` 开头的目录）：

```powershell
dotnet run --project .verify/e2e/E2E.csproj
```

`.verify/` 里是一份 UnityEngine 桩 + 测试程序，只用于离线校验，**不属于交付内容**，
删掉不影响运行。

---

## 七、下一步可以加什么

1. **UDP 可靠层**：现在 `UdpSnapshot` 是纯不可靠的，丢包靠客户端插值掩盖。
   要做命中判定/开门这类事件，建议加一个带 ack + 重发的可靠通道。
2. **客户端预测 + 回滚**：`UdpInput.tick` 和 `PlayerState.last_input_tick` 已经预留好了字段，
   本地立刻响应当前输入，收到快照后用 `last_input_tick` 之后的输入重放校正。
3. **实体插值**：快照缓冲 100ms 再渲染，抖动会小很多。
4. **断线重连**：`sessionToken` 已经是稳定标识，房主可以保留会话一段时间等待重连。
5. **房间密码**：`JoinRequest.room_password` 字段已预留。
6. **局域网房间发现**：UDP 广播（`UdpHostServer.EnableBroadcast` 已经打开）做自动搜索房间列表。
