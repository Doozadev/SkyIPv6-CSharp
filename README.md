# SkyIPv6-CSharp

给自己服务器用的 Cloudflare IPv6 DDNS 工具，C# 写的。

国内三大运营商（尤其电信）IPv6 前缀经常轮换，大部分脚本要么选错地址，要么卡死 100 秒。  
这个程序目前自己用着没翻车，拿出来分享一下，有缘人觉得有用就一起完善。

**核心优势（自用实测）**
- 永远选 Preferred Lifetime 最长的地址（运营商当前真正有效的）
- 支持代理 + 代理挂了自动降级直连
- 超时 20 秒，永不卡死
- 单文件发布，几 MB 扔服务器直接跑
- 完整日志 + systemd timer 一键部署

**重要说明**
- 当前仅支持 Linux 系统（已测试 Ubuntu / Debian / Rocky / CentOS）
- 依赖系统自带的 `ip` 命令（iproute2 工具集），几乎所有现代发行版默认都自带  
  检查方法：`ip -6 addr show` 能正常输出即代表可用

## 快速部署（推荐：直接用预编译二进制）

项目里已经放了编译好的单文件 `deploy-package/CfDdnsClient`，可以跳过编译直接用：

```bash
# 1. 克隆项目
git clone https://github.com/Doozadev/SkyIPv6-CSharp.git
cd SkyIPv6-CSharp

# 2. 把二进制传到服务器（推荐路径）
sudo mkdir -p /opt/cfddns/bin
sudo cp deploy-package/CfDdnsClient /opt/cfddns/bin/
sudo chmod +x /opt/cfddns/bin/CfDdnsClient

# 3. 创建配置文件（务必改成自己的！）
sudo cp Config.json.example /opt/cfddns/Config.json 2>/dev/null || touch /opt/cfddns/Config.json
sudo vim /opt/cfddns/Config.json
```

最小可用 `Config.json` 示例：

```json
{
  "provider": "cloudflare",
  "zoneName": "example.com",
  "recordName": "home",
  "interfaceName": "ens18",
  "ttl": 300,
  "proxy": "http://127.0.0.1:7890",
  "httpTimeoutSeconds": 20,
  "cloudflare": {
    "apiToken": "你的 Cloudflare API Token（只给 DNS Edit 权限）",
    "proxied": false
  }
}
```

**重点：永远不要把真实 token 提交到 git！**

```bash
# 4. 手动测试
sudo /opt/cfddns/bin/CfDdnsClient run --config /opt/cfddns/Config.json
```

看到 `SUCCESS` 或 `already up-to-date` 就说明成功了。

## 完整部署（自己编译最新版）

想用最新代码或改功能时自己编译：

```bash
# 1. 克隆项目
git clone https://github.com/Doozadev/SkyIPv6-CSharp.git
cd SkyIPv6-CSharp

# 2. 安装 .NET 8（只做一次）
chmod +x dotnet-install.sh
sudo ./dotnet-install.sh --channel 8.0 --install-dir /usr/local/bin/dotnet-latest
sudo ln -sf /usr/local/bin/dotnet-latest/dotnet /usr/bin/dotnet
dotnet --info

# 3. 编译单文件发布包
dotnet publish -c Release \
  -r linux-x64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true \
  /p:DebugType=None \
  /p:DebugSymbols=false \
  -o ./deploy-package

# 4. 打包（方便上传）
tar -czf cfddns-deploy-$(date +%Y%m%d-%H%M).tar.gz ./deploy-package

# 5. 上传并解压到服务器
scp cfddns-deploy-*.tar.gz root@your-server:/root/
# 服务器上执行：
sudo mkdir -p /opt/cfddns/bin
sudo tar -xzf /root/cfddns-deploy-*.tar.gz -C /opt/cfddns/bin/
sudo chmod +x /opt/cfddns/bin/CfDdnsClient
```

## systemd 定时任务（每 5 分钟运行一次）

```bash
# 创建 service
sudo tee /etc/systemd/system/cfddns.service > /dev/null <<'EOF'
[Unit]
Description=Cloudflare IPv6 DDNS Update
After=network-online.target

[Service]
Type=oneshot
WorkingDirectory=/opt/cfddns
ExecStart=/opt/cfddns/bin/CfDdnsClient run --config /opt/cfddns/Config.json
StandardOutput=append:/var/log/cfddns.log
StandardError=append:/var/log/cfddns.log
EOF

# 创建 timer
sudo tee /etc/systemd/system/cfddns.timer > /dev/null <<'EOF'
[Unit]
Description=Run Cloudflare DDNS every 5 minutes

[Timer]
OnBootSec=1min
OnUnitActiveSec=5min
Persistent=true

[Install]
WantedBy=timers.target
EOF

# 启用
sudo systemctl daemon-reload
sudo systemctl enable --now cfddns.timer

# 查看状态
systemctl list-timers | grep cfddns
tail -f /var/log/cfddns.log
```

## 常见问题

- 程序卡住不退出 → 代理问题，已默认 20 秒超时，不会卡死  
- 选错 IPv6 地址 → 不会，永远选 Preferred Lifetime 最长的  
- 提示找不到 `ip` 命令 → 安装 iproute2：`sudo apt install iproute2` 或 `sudo yum install iproute`  
- 想改成 10 分钟运行一次 → 修改 timer 的 `OnUnitActiveSec=10min`  
- 查看详细日志 → `tail -f /var/log/cfddns.log`

就这些，能跑就行，不折腾  
有问题欢迎提 Issue，一起完善

https://github.com/Doozadev/SkyIPv6-CSharp
