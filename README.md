# Hysteria2 Realms 联机（PCL Nex 插件）

一个让 PCL Nex 用户通过 Hysteria2 Realms 的 STUN、牵线服务和 UDP 打洞能力进行 Minecraft Java 版联机的插件。双方在没有公网 IP 或端口转发时尝试建立 QUIC P2P 直连，并把 Minecraft Java 版 TCP 流量封装在连接中。

## 功能特性

- 固定使用 Hysteria `2.10.0` 的公开 `realm://public@realm.hy2.io/<随机名称>` 模式；分享时只传递 67 位紧凑载荷，不携带协议头、Realm 服务地址或参数名。
- 首次使用从官方 GitHub Release 下载 Windows 内核，并执行固定 SHA-256 校验。
- 为房主生成本地自签名证书；加入端同时使用 `insecure: true` 与 `pinSHA256`，证书固定仍可阻止中间人替换服务端。
- 每次创建联机生成随机会话种子，并分别派生 Realm 名称、Hysteria 认证密码与 Salamander 混淆密码；紧凑联机码不再重复传递这些字段。
- 服务端 ACL 仅允许访问房主本机本次开放的 Minecraft TCP 端口，并拒绝所有其他 TCP/UDP 请求，避免被作为通用代理使用。
- 加入端入口仅监听 `127.0.0.1`，并通过 Minecraft LAN 广播显示在多人游戏列表中，也可直接连接页面显示的 `127.0.0.1:端口`。
- Hysteria 子进程加入 Windows Job Object，启动器退出时会一并清理；包含密钥的临时配置会在断开后删除。

插件不会为下载或 Realm 控制面设置代理。可以用环境变量 `HYSTERIA_PATH` 指向哈希匹配的现有 Hysteria 可执行文件。

## 手动安装

1. 在 GitHub Releases 页面下载最新版本的 `.pclx`。
2. 打开 PCL Nex 的「设置 -> 插件」，安装该 `.pclx` 文件。
3. 重启 PCL Nex 后插件生效。

## 使用

1. 房主进入 Minecraft 世界并选择「对局域网开放」。
2. 打开「百宝箱 -> Hysteria P2P」，选择检测到的世界并创建联机。
3. 房主复制 67 位紧凑联机码并通过可信渠道发送给好友。联机码包含认证密钥，不应公开。
4. 加入者粘贴联机码。插件会创建本地 TCP 入口并广播到 Minecraft 局域网服务器列表，也可以直接连接页面显示的 `127.0.0.1:端口`。

## 注意事项

- 只支持 Minecraft Java 版 TCP 联机，不支持基岩版 UDP 联机。
- `realm.hy2.io` 是无可用性保证的公益牵线服务。牵线服务只参与连接建立，不中转游戏流量，但会看到双方用于打洞的公网地址。
- UDP 打洞受 NAT 类型影响；任一侧为随机对称 NAT 时通常无法直连。
- 联机期间双方都需要能访问牵线服务、STUN 服务，并允许出站 UDP。
- 本插件不会绕过 Minecraft 的正版认证、服务器白名单或任何游戏认证机制。

## 从源码构建

需要 Windows、.NET 10 SDK 以及工作区 `sdk/2026.07.2/x64` 中的 PCL2-Nex SDK DLL（从官方发布包提取）：

```powershell
.\scripts\pack.ps1 -Version 1.0.0
```

产物输出到 `artifacts/xjh2009.hysteria.link-<版本>-anycpu.pclx`。仓库已配置 GitHub Actions：推送 `v*` 标签即可自动从 PCL2-Nex 官方 Release 提取 SDK、构建、测试并发布 Release。

## 许可证

本项目自身代码使用 [Mozilla Public License 2.0](LICENSE)。Hysteria 内核属于其相应权利人，会在首次联机时下载到 PCL 插件数据目录。
