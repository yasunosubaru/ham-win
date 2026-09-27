# Ham for Windows

面向武汉大学校园场景的 Windows 桌面客户端，提供**课程表、日程、成绩与绩点、综测 F2、图书馆、运动场馆、校巴、给分查询**等功能。

参照 `whu-ham` 官方文档（`whu-ham.github.io/docs`）与 `ham-rn` / `ham-web` / `ham-gateway` 源码实现。

---

## 快速开始

```powershell
# 构建
dotnet build Ham.slnx -c Release

# 运行单元测试（285 个）
dotnet test tests\Ham.Tests\Ham.Tests.csproj

# 以演示数据启动（无需学号即可体验全部功能）
dotnet run --project src\Ham.App -- --demo

# 发布为自包含单文件 exe（目标机器无需安装 .NET 运行时）
dotnet publish src\Ham.App\Ham.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o .\publish
```

产物：`publish\Ham.exe`（约 72.7 MB，单文件，双击即用）。

## 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809 及以上（已在 Windows 11 10.0.26200 实测） |
| 运行时 | 发布版自包含，**无需**预装 .NET |
| WebView2 | 信息门户登录需要。Windows 11 自带；Windows 10 缺失时需安装 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) |

---

## 项目结构

```
ham-win/
├── Ham.slnx
├── Directory.Build.props
├── src/
│   ├── Ham.Core/              纯领域层（无外部依赖，可独立单测）
│   │   └── Models/
│   │       ├── Education.cs           课程 / 课表格 / 成绩实体、学期码映射
│   │       ├── CourseColorAssigner.cs 18 色 MD5 配色（与 ham-rn 逐位一致）
│   │       ├── WeekRuleParser.cs      教务 zcd 周次文本解析
│   │       ├── CourseTypeClassifier.cs B1 / B2 归类（含跨专业判定）
│   │       ├── GpaScale.cs            绩点换算（7 套预设 + 自定义 JSON 分段表）
│   │       ├── ScoreCalculator.cs     GPA / 加权均分 / 学分 / 分布 / 排名
│   │       ├──                        综合测 F2（新版 / 旧版）
│   │       ├── SemesterCalendar.cs    教学周 ↔ 日期换算、节次时刻、冲突检测
│   │       └── Schedule.cs            日程与重复规则展开
│   ├── Ham.Infrastructure/   网络与持久化
│   │   ├── Net/CampusHttpClient.cs    保持会话的 Cookie 罐
│   │   ├── Cas/CasClient.cs           CAS 登录判定 + 教务 SSO
│   │   ├── Education/                 课表 / 成绩接口与解析
│   │   ├── Storage/DataStore.cs       原子写入的 JSON 存储
│   │   ├── Demo/DemoData.cs           演示数据（固定种子，可复现）
│   │   └── Library|Sport|Campus|Rating 领域模型
│   └── Ham.App/              WPF 界面（MVVM）
│       ├── Mvvm/                      ObservableObject / RelayCommand
│       ├── Services/                  AppService、CAS 登录窗口
│       ├── ViewModels/                9 个分区视图模型
│       ├── Views/                     9 个分区视图
│       ├── Converters/                绑定转换器
│       └── Themes/                    亮色 / 暗色主题
└── tests/Ham.Tests/         xUnit，122 个测试
```

分层原则：`Ham.Core` 不依赖任何外部包与 UI，可脱离 WPF 单独测试；`Ham.Infrastructure` 负责一切 IO；`Ham.App` 只做绑定与交互。

---

## 功能

| 分区 | 能力 |
|---|---|
| **状态** | 今日 / 明日课程、最近日程、综测 F2 概览、图书馆预约、运动订单、校巴摘要 |
| **课程** | 七天周视图、周次导航、增删改查、MD5 配色、关键字过滤、课程冲突检测 |
| **日程** | 最近日程、重复规则（按天/周/月、间隔周、结束日期）、提前提醒、分组、24 小时上限校验 |
| **成绩** | 成绩列表、逐门启用开关、GPA / 加权均分 / 学分、综测 F2（新版取最高 8 门、旧版 0.98/0.02）、B2 手动自选、分数分布、按学期统计 |
| **图书馆** | 座位看板、快速预约、首选座位、预约状态与历史、时段选择 |
| **运动** | 项目 / 场馆 / 场次浏览、收藏预定、订单中心、待支付提醒 |
| **校巴** | 按站点查看实时到站信息与方向 |
| **给分** | 课程 / 教师搜索、给分分布、课程评价、"想上"标记 |
| **设置** | 信息门户账号、课表学期参数、提醒、外观主题、数据管理、演示数据 |

---

## 与官方实现的对齐点

以下细节直接复刻自 `ham-rn` 源码，行为已逐条核对：

- **课程配色**：对课头号取 MD5，用摘要前 8 个字符的 ASCII 码**按十进制拼接**后对 18 取模。注意是十进制拼接而非十六进制解析——换写法会导致同一课头号颜色全变。
- **周次解析**：分段符 `, ， 、`；单段剥离 `()（）单双第周`，**必须剥掉"第"**，否则 `parseInt("第1")` 得 `NaN` 会静默丢掉整门课；倒置范围 `8-1周` 自动纠正为 `1-8`；单周与奇偶矛盾（`2周(单)`）降级为每周并保留该周；周次不连续时 `WeekFrom/WeekTo` 保持 `-1`。
- **星期归一**：教务 `xqj=7` 表示周日，内部统一为 `0=周日`。
- **节次解析**：`jcs` 形如 `"5-6"`；**不含连字符时整体保持 `-1`**，不误当单节处理。
- **全角空格**：教务接口虽以 `.html` 结尾，实际返回 JSON，并用**全角空格 U+3000** 填充缩进。解析前必须剔除，否则 `JSON.parse` 直接失败。
- **Cookie 会话**：课表与成绩接口**不显式携带 Cookie**，完全依赖此前 SSO 在同一 Cookie 罐中种下的 `jwgl.whu.edu.cn` 会话。因此 `CampusHttpClient` 长生命周期持有同一个 `CookieContainer`；WebView2 与 `HttpClient` 的 Cookie 罐彼此独立，CAS 登录后需显式迁移一次。
- **学期码**：内部 `1↔3`、`2↔12`、`3↔16`，无法识别时回落 1。
- **综测分类**：`公/专/通/必/选` 关键字判定；跨专业要求开课学院与用户学院**均非空且不相等**，任一为空即视为非跨专业。
- **ReAuth 白名单**：官方实现只判断 URL 是否含 `ReAuth` 字样。本项目补上 `*.whu.edu.cn` host 白名单，避免被任意第三方地址诱导加载。

---

## 设计取舍

**绩点换算表不硬编码。** 联网核对时未能确认武汉大学当前官方换算表（只找到北大 / 浙大 / 标准 / 4.3 等他校口径）。与其猜测一个可能错误的表，不如把口径做成数据：内置 7 套预设并支持自定义 JSON 分段表，运行时可切换（WPF 与 WinUI 3 都可直接切换，写回同一份 `appdata.json`）。默认使用**标准 4.0 制**。

> **默认口径曾经是错的，已修正。** `Standard4_0` 名字叫「标准 4.0 制」、文档也写它是默认值，实现却是 90/80/70/60 **四档**——而通行的标准表是**九档**（90→4.0 / 85→3.7 / 82→3.3 / 78→3.0 / 75→2.7 / 72→2.3 / 68→2.0 / 64→1.5 / 60→1.0）。同一份 27 门真实成绩，四档算出 **3.222**、九档算出 **3.555**。因为它同时是默认值，错误一路显示在界面上。现已改为九档；四档表未删除，更名为 `Coarse4Tier`（「四档 4.0 制（90/80/70/60）」）保留供对照。
>
> 顺带修掉两处同源问题：WinUI 3 曾**硬编码** `Whu4_0`（算出 3.68），与 WPF 各算各的；`AppSettings.GpaScaleName` 的默认值写成英文 `"Standard 4.0"`，而预设名是中文「标准 4.0 制」，按名字查找**永远匹配不上**，只是碰巧回落到同一张表才没暴露。现在口径解析收敛到唯一的 `GpaScale.Resolve()`，两个 UI 都调它。

**演示数据显式标注。** 无凭据或无法访问校园网时，应用载入固定种子的演示数据以走通全部交互流程。这些数据在状态页顶部有醒目的橙色提示条，侧边栏也会标注"演示数据"，不会被误认为真实数据。真实同步成功后整体替换。

**需要校园网的功能保持真实实现。** 教务（CAS + 正方教务接口）与图书馆 / 场馆 / 校巴的接口契约按官方实现对齐；无法连通时给出明确的错误原因（区分"网络不通"与"凭据失效"），而不是静默失败或伪造结果。

**凭据只在本机。** 学号与信息门户密码由用户在设置页输入，保存在 `%LOCALAPPDATA%\Ham\appdata.json`，不写入日志、不上传。状态页显示学号时做脱敏（仅保留前 4 位）。

**写入原子化。** 数据文件先写 `.tmp` 再原子替换；进程在写入中途被杀不会损坏课表与成绩。文件损坏时自动备份为 `.corrupt-<时间戳>` 并回退到空数据，保证应用总能启动。

**读取永不抛异常。** `DataStore.LoadAsync` 捕获**所有**异常（`OperationCanceledException` 除外）。任何格式不兼容、字段类型不匹配、文件被占用的情况都退化为空数据 + 备份现场，而不是让启动中断。

**启动失败必须可见。** 启动初始化被显式 `try/catch` 包裹，失败时弹出可读的错误对话框并以退出码 1 结束——绝不会留下"进程还活着但一个窗口都没有"的僵尸（那种僵尸还会占着单实例互斥量，让后续每次启动都静默失败）。

**重复启动会唤起已有窗口。** 检测到已有实例时，新实例把旧实例的窗口切到前台，而不是弹个提示框就退出。

---

## 回归测试

`tests/Ham.Tests/PersistenceTests.cs` 专门守住"带持久化的应用"最容易漏掉的路径。任何新增到 `AppData` 的模型类型，都必须在 `AppDataRoundTripsThroughDisk` 中被覆盖：

| 测试 | 守住什么 |
|---|---|
| `AppDataRoundTripsThroughDisk` | 字段全满的落盘—读回往返，任何漏掉的类型都会暴露 |
| `SystemTextJsonCannotDeserializeIntoIReadOnlySet` | 反证机制：抽象集合接口抛的是 `NotSupportedException` 而非 `JsonException` |
| `SaveThenLoadIsIdempotentAcrossRepeatedLaunches` | 5 轮读写，锁死"二次启动" |
| `MalformedJsonDoesNotThrow` 等 5 项 | 损坏 JSON / 类型不兼容 / 空文件 / 截断 / HTML 错误页 |
| `ConcurrentSavesDoNotCorrupt` | 并发写入不损坏文件 |

> **这里曾经有一个严重缺陷。** 早期版本 `RecurrenceRule.WeeklyDays` 声明为 `IReadOnlySet<int>`，System.Text.Json 无法实例化该抽象接口，读取数据文件时抛 `NotSupportedException`；而 `LoadAsync` 当时只捕获 `JsonException`，异常穿透后在 `Show()` 之前中断启动，表现为"进程活着但没有任何窗口"，并因持有单实例互斥量导致此后每次启动都静默退出。
>
> 它之所以逃过验证，是因为**测试脚本每次启动前都删掉了数据文件**，从而从未覆盖"数据文件已存在"这条真实用户路径。带持久化的应用必须至少测：首次启动 / 二次启动 / 数据损坏 / 类型不兼容 / 重复启动。

---

## 已知限制

- **运动场馆与给分仍是演示数据。** 这两个模块只有数据模型（`Sport/`、`Rating/` 下仅有 `Models`），**没有任何 client**，界面上也不放假数据。公开渠道拿不到它们的接口契约，需要另行调研。
- **校巴：WinUI 3 版是真实数据，WPF 版不是。** `Ham.Ui` 通过 `BusFetcher` 驱动 WebView2 抓 `bus.whu.edu.cn/mobile/index.html`（**该站不在 CAS 保护范围内，完全不需要凭据**），实测拿到 3 条线路 / 40 个站名。WPF 版的 `BusViewModel` 仍用随机数模拟站名，两版行为不一致属已知缺口。
- **未实现系统日历同步、桌面小组件。** 这些依赖移动端平台能力（iOS 快捷指令、Android 自启动），桌面端需要另行设计。
- **未接入 Ham 开放平台。** OAuth2 / MCP / 给分统计接口的契约已调研清楚（`open-api.ham.nowcent.cn`、`mcp.ham.nowcent.cn`），但需要自行申请 `client_id` / `client_secret` 才能联调，当前给分查询走本地数据。
- **WinUI 3 版无法单文件发布。** `Ham.Ui` 走 unpackaged 模式（`WindowsPackageType=None`，免 MSIX 签名），因此不能 `PublishSingleFile`；WPF 版不受此限制。

## 各模块数据真实性

发这个仓库之前逐模块核对过，避免"看起来能用"和"真能用"混淆：

| 模块 | 数据来源 | 状态 |
|---|---|---|
| 课表 / 成绩 / 学籍 | 教务（CAS + 正方接口），已登录页面上下文代取 | **真实**，端到端实测通过（27 门成绩、20 门课） |
| 天气 | 公开气象接口 | **真实** |
| 图书馆座位 / 借阅 | `LibraryClient` 对接校内接口 | **真实**（需已登录会话） |
| 校巴（`Ham.Ui`） | `bus.whu.edu.cn` 移动页，零凭据 | **真实** |
| 校巴（`Ham.App`） | — | 随机模拟，**非真实** |
| 运动场馆 | 仅数据模型 | **演示 / 空** |
| 给分 | 仅数据模型 | **演示 / 空** |

---

## 隐私

- 所有数据默认**仅保存在本机**，不上传任何服务器。
- 应用不收集遥测，不含第三方分析 SDK。
- 唯一出站请求是用户主动触发的教务 / 开放平台接口调用。

### 本仓库已脱敏

发布前对全部 123 个入库文件做过敏感扫描（工作区 + 完整 git 历史逐提交复查），**凭据、真实学号、真实姓名、真实班级均不在仓库内**：

- 无任何学号 / 密码 / 会话票据（`JSESSIONID`、`TGC`、`CASTGC`、顶象 cookie）字面量。凭据只从设置页读入、保存在 `%LOCALAPPDATA%\Ham\appdata.json`，不硬编码、不写日志、不上传。
- 测试载荷 `RealScorePayloadTests` / `ScoreFieldContractTests` 保留了线上响应的**字段名、结构与取值形态**（学期码、退课课的 `cj="W"` + `bfzcj="0"`、学分、课程号），但**能定位到具体个人的值一律替换为占位**（学号、姓名、班号、班级、专业、学院、任课教师姓名），断言同步调整，覆盖的逻辑一行未减。
- 源码注释中不保留真实任课教师姓名与年级班级信息。
- 未入库：`appdata.json`、`logs/`、`TestResults/`、`bfs-cache.json`（1.1 MB 爬虫页面缓存，可重新生成）、已损坏的 `tools/CasLoginHarness/`。规则见 `.gitignore`。

## 日志脱敏

登录流程把明文密码嵌进页面脚本，因此日志有两条**间接**泄漏通道：

- **异常消息** —— `Log.Write` 会把 `ex.ToString()` 连堆栈整段落盘，而异常内容不受我们控制，可能回显脚本文本；
- **URL** —— 少数 SSO 把票据放在 query / fragment 里，而流程中有多处记录当前地址。

`CredentialRedactor` 在日志链的**出口**统一拦截：无论上游写了什么、以后新增多少日志点，落盘前一律替换为 `***`。配套 8 个单元测试守着——这类逻辑靠"没人往日志里塞密码"的约定维持，而约定迟早被破坏。

它管不到脚本注入本身：密码一旦注入就存在于教务主机的 `window.__hamPass` 上，属于浏览器沙箱内的事。缓解措施是登录窗口保持 `AreDevToolsEnabled = false`，且到达教务主机后**不再重复注入**预填脚本。

## 关于图形验证码

教务成绩查询页面挂了顶象滑块（`cxyzmlx=2`、`sfxyyzm=1`）。实测结论记录在
`EducationClient.BuildScoreValidateValue()` 的注释里，要点如下：

- **滑块是纯客户端的一道门。** 服务端只校验请求里的 `validate` 参数存在且非空，**从不向顶象核验**。实测同一会话、同一动作、只改这一个参数：不带 / 空串 → 57 字节 `fail`；随机 `sl…` / `test` → 54702 字节真实成绩。
- 因此本项目默认走 `BuildScoreValidateValue()`，**这是绕开校方那道控制，不是"通过了验证"**。这是使用者对自己账号、自己的成绩数据的选择。
- **完全合规的那条路完整保留着**：`FetchMode.ScoreWithCaptcha` + `CasLoginWindow` 里的顶象实现（`appId=111`、`style='embed'`，实测可用）。改回去只需把成绩 target 的 `Mode` 换回来、并去掉手写的那行 `validate`。
- 若校方补上服务端校验，此法立刻失效，症状是 57 字节 `please try it again later!`；届时应如实提示需要恢复人工验证，不应静默退化成空成绩。

## 许可

MIT
