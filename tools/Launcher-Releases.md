# PCL XH 发布与更新

当前程序为 **DeveloperBeta · Build 26.01030**。发布地址固定为
<https://github.com/NoSetViolin/PCL-XH/releases>。

## 三个通道怎么发布

每个版本创建一条独立的 GitHub Release，tag 用下列格式：

| 通道 | Tag 示例 | GitHub Pre-release |
| --- | --- | --- |
| DeveloperBeta | `dev-26.01030`、`dev-26.01101` | 勾选 |
| PublicBeta | `beta-26.0.1`、`beta-26.0.2` | 勾选 |
| Release | `26`、`26.0.1`、`26.1` | 不勾选 |

在 GitHub 的 Releases 页面点击新建 Release，创建相应 tag，上传
`xiaohanlaucher.exe`，填写说明后发布。三个通道可以使用相同的 EXE 文件名。
不要勾选 Draft 后就停止发布：草稿不会参与更新检查。每次更新都递增 tag，
不要通过覆盖旧 tag 的附件来表示新版本。只创建 Git tag 而不发布 Release 也不会触发更新。

启动器优先按 `dev-` / `beta-` 前缀识别通道。兼容旧的裸 build tag
`26.01030` 为 DeveloperBeta；不带通道前缀的普通数字 tag 为 Release。
普通数字 tag 如果勾选 GitHub Pre-release 则视为 PublicBeta，但推荐始终使用
明确的 `beta-` 前缀。没有 EXE 附件时能显示版本信息，但不能下载。

版本号按每一段数字比较，省略的尾段按零处理：
`26 < 26.0.1 < 26.0.2 < 26.1 < 26.1.1 < 26.1.2 < 26.2 < 26.10`。
同通道只提示比当前版本更高的 tag；跨通道是用户主动切换，不比较 Beta build
和正式版号的数值大小。发布时间不决定版本顺序。

## 编译时写入当前通道与版本

默认不设置 `LauncherReleaseVersion`，程序显示 DeveloperBeta 与当前 build。
在对应源码版本编译时，可以用 MSBuild 参数写入即将发布的完整 tag：

```powershell
dotnet publish 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c Release -r win-x64 --self-contained false -p:LauncherReleaseVersion=dev-26.01030
dotnet publish 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c Release -r win-x64 --self-contained false -p:LauncherReleaseVersion=beta-26.0.1
dotnet publish 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c Release -r win-x64 --self-contained false -p:LauncherReleaseVersion=26.0.1
```

只执行与你正在发布的通道对应的一条命令。保留项目已有的凭据注入与单文件发布参数。
Build 号仍由项目的版本元数据维护，不必为了 PublicBeta/Release tag 修改 build。

即使 EXE 未写入完整 tag，检查更新时也会用 GitHub 的附件 SHA256 digest
匹配当前正在运行的 EXE，识别其已发布的通道和 tag，避免反复下载同一个版本。
正确写入编译参数仍可保证首次打开页面或离线时显示准确的发布版本。

## 使用与验证

在“设置 → 关于”页查看当前版本，选择更新通道，点击检查更新。发现版本后，
点击蓝色“更新并重启”即可自动下载、核对大小与 GitHub 提供的 SHA256、等待其他
下载任务结束，并正常关闭当前启动器。独立更新进程等待旧进程退出，再原子替换
原路径的 EXE，启动新版。新版进入 Running 状态且稳定运行后才删除旧 EXE 备份。
无需用户选择保存位置、手动重启或删除旧文件；配置与游戏目录不参与替换。

若新版启动失败、提前退出或未在期限内确认启动成功，更新进程会恢复旧 EXE
并重新启动旧版。失败诊断保留在安装目录下本次 `.pcl-update-*` 子目录。
系统未允许写入安装目录或未能启动更新进程时，当前启动器继续运行并显示失败原因。
取消下载会清理本次临时文件。更新流程不会提升权限。

“下载 EXE”保留为手动下载入口；开发环境的多文件 apphost 不能直接单文件替换，
需使用手动下载。发布给用户的版本应为本项目的单文件 EXE，并保留启动确认协议，
使自动更新能够确认成功后清理旧文件。更新成功后，EXE 旁的版本记录文件保存
对应 tag 与 SHA256，保证新版离线时也能识别自己的通道与发布版本。

`launcher-update-only.flag` 为隔离 UI 测试入口，报告为 `launcher-update-report.txt`。
它用模拟附件测试三通道、下载和错误状态，最后读取真实仓库 Release API。
单元测试为 `GitHubReleaseUpdaterTest`，覆盖版本比较、通道、分页、架构与下载校验。

`AtomicLauncherUpdateTest` 使用 `tools/self-update-smoke` 的两个单文件 EXE
测试真实 Windows 进程占用、自动重启、备份清理、崩溃/超时回滚、取消及篡改拦截。
先分别用 `-p:FixtureGeneration=old` / `new` 发布该测试项目到独立目录，设置
`PCL_SELFUPDATE_FIXTURE_OLD` / `PCL_SELFUPDATE_FIXTURE_NEW` 为相应 EXE 的绝对路径。
所有替换、删除与进程测试都在独立的 `pcl-self-update-tests` 临时目录中进行。

`auto-launcher-update-only.flag` 用于真正的发布版启动器端到端测试，需将带诊断注入的
两个单文件版本放在隔离目录，设置 `PCL_FRAME_QA_ROOT` 与 `PCL_AUTOUPDATE_NEW_EXE`。
旧版会通过正式页面的自动更新流程下载测试附件并退出，新版确认启动、验证原路径、
tag、SHA256 与旧备份清理后写入 `auto-launcher-update-report.txt`。不会运行真实实例。

GitHub 接口参考：
- <https://docs.github.com/en/rest/releases/releases#list-releases>
- <https://docs.github.com/en/rest/releases/assets#get-a-release-asset>
