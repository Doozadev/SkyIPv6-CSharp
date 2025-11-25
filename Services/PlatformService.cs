// Services/PlatformService.cs
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using CfDdnsClient.Models;

namespace CfDdnsClient.Services
{
    public class PlatformService
    {
        private const string CacheFileSuffix = ".lastip";
        
        // --- 核心 IP 解析逻辑 (ip command) ---
        public async Task<List<IPv6Info>> GetAvailableIPv6(string interfaceName)
        {
            var command = "ip";
            var arguments = $"-6 addr show dev {interfaceName}";

            var outputString = await RunCommandAsync(command, arguments);

            var ips = new List<IPv6Info>();
            // 修正后的正则表达式
            const string IPv6Regex =
                @"inet6\s+(?<ip>[\w\:]+)/(\d+)\s+scope\s+(?<scope>\w+)(?:\s+(?<flags>.*?))?\s+valid_lft\s+(?<valid>\w+)\s+preferred_lft\s+(?<preferred>\w+)";
            var matches = Regex.Matches(outputString, IPv6Regex, RegexOptions.Multiline | RegexOptions.CultureInvariant);

            foreach (Match m in matches.Cast<Match>())
            {
                var ip = m.Groups["ip"].Value;
                var scope = m.Groups["scope"].Value;
                var flags = m.Groups["flags"].Value;

                var validLft = ParseLifetime(m.Groups["valid"].Value);
                var preferredLft = ParseLifetime(m.Groups["preferred"].Value);
                
                // 1. 解析 AddressState
                var addressState = "Preferred/Static";
                if (flags.Contains("dynamic"))
                {
                    addressState = "Preferred/Dynamic";
                }
                else if (flags.Contains("deprecated"))
                {
                    addressState = "Deprecated";
                }

                // 2. 确定 DDNS 资格所需的布尔值
                bool isDeprecated = flags.Contains("deprecated");
                // ULA 地址以 fc00::/7 开头，但常用的范围是 fc00::/8，此处简化判断
                bool isULA = ip.StartsWith("fc00:", StringComparison.OrdinalIgnoreCase) || ip.StartsWith("fd00:", StringComparison.OrdinalIgnoreCase);

                // 3. 确定是否为 DDNS Candidate
                bool isCandidate = scope == "global" &&
                                   !isULA &&
                                   !isDeprecated;
                
                // 4. 构建 IPv6Info
                ips.Add(new IPv6Info(
                    IP: ip,
                    Scope: scope == "global" ? "Global Unicast" : "Link Local", // 修正 scope 输出
                    AddressState: addressState,
                    ValidLft: validLft,
                    PreferredLft: preferredLft,
                    IsCandidate: isCandidate,
                    IsULA: isULA,
                    IsDeprecated: isDeprecated
                ));
            }

            return ips;
        }

        // --- IP 选择逻辑 (SelectIndex) ---
        public string SelectBestIPv6(Config config, List<IPv6Info> availableIps)
        {
            var candidates = availableIps
                .Where(ip => ip.IsCandidate)
                .ToList();
            if (candidates.Count == 0)
            {
                Logger.Fatal("No suitable global IPv6 addresses found to use for DDNS.");
            }

            // 1. 检查 'selectIndex' 索引 (0 表示自动选择)
            if (config.SelectIndex > 0)
            {
                int index = config.SelectIndex - 1; // 转换为 0-based 索引
                if (index < candidates.Count)
                {
                    Logger.Info("Using configured 'selectIndex' ({0}) IP: {1}", config.SelectIndex, candidates[index].IP);
                    return candidates[index].IP;
                }

                Logger.Fatal($"Configured 'selectIndex' ({config.SelectIndex}) is out of range. Only {candidates.Count} candidates available.");
            }

            // 2. 默认选择 (SelectIndex == 0): 寻找 Preferred Lifetime 最长的 IP
            var bestIP = candidates
                .OrderByDescending(ip => ip.PreferredLft)
                // 其次按 IP 地址族排序 (InterNetworkV6)
                .ThenByDescending(ip => IPAddress.Parse(ip.IP).AddressFamily) 
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Internal error: Failed to select best IP from candidates.");

            Logger.Info("Auto-selected IP with longest Preferred Lifetime ({0}): {1}", FormatTimeSpan(bestIP.PreferredLft), bestIP.IP);
            return bestIP.IP;
        }

        // --- 新增外部 IP 获取逻辑 ---
        public async Task<string> GetExternalIPv6(List<string> apiUrls)
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            foreach (var url in apiUrls)
            {
                try
                {
                    Logger.Info("Trying external API: {0}", url);
                    // 假设 API 返回的只是 IP 地址，可能包含回车或空格
                    var response = await httpClient.GetStringAsync(url);
                    var ip = response.Trim(); // 去除回车和空格

                    // 验证是否是有效的 IPv6 地址
                    if (IPAddress.TryParse(ip, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        Logger.Success("Successfully retrieved IPv6: {0} from {1}", ip, url);
                        return ip;
                    }
                    Logger.Error("API {0} returned an invalid IPv6 address or content: {1}", url, ip);
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to query external API {0}. Error: {1}", url, e.Message);
                }
            }

            Logger.Fatal("Failed to get a valid global IPv6 address from all configured external APIs.");
            return ""; // 实际上 Fatal 会退出程序
        }

        // --- 辅助方法 ---

        // (RunCommandAsync, ParseLifetime, FormatTimeSpan, IP 缓存读写等方法保持不变)
        private async Task<string> RunCommandAsync(string command, string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            return output;
        }
        
        private TimeSpan ParseLifetime(string value)
        {
            if (value.ToLowerInvariant() == "forever")
                return TimeSpan.MaxValue;
            
            // 修正：删除所有非数字字符（例如 's', 'sec', 'valid', 'preferred' 等）
            // 只保留纯数字，然后尝试解析为秒数。
            var numericValue = new string(value
                .Where(c => char.IsDigit(c) || c == '.')
                .ToArray());
                
            if (long.TryParse(numericValue, out var seconds))
                return TimeSpan.FromSeconds(seconds);
            
            return TimeSpan.Zero;
        }

        public string FormatTimeSpan(TimeSpan ts)
        {
            if (ts == TimeSpan.MaxValue) return "forever";
            if (ts == TimeSpan.Zero) return "0s";
            return ts.TotalSeconds + "s";
        }

        public string GetCacheFilePath(Config config)
        {
            string dir;
            
            if (!string.IsNullOrEmpty(config.WorkDir))
            {
                // 1. 如果用户提供了 WorkDir，使用其绝对路径
                dir = Path.GetFullPath(config.WorkDir);
            }
            else
            {
                // 2. 如果 WorkDir 为空，使用 Config.json 所在的目录
                if (string.IsNullOrEmpty(config.ConfigFilePath))
                {
                    // 理论上 Config.Load 确保了 ConfigFilePath 不为空，但以防万一
                    Logger.Fatal("Cannot determine cache directory: Config file path is missing.");
                }

                // 获取配置文件的目录。
                dir = Path.GetDirectoryName(config.ConfigFilePath)!;
                
                if (string.IsNullOrEmpty(dir))
                {
                    // 这种情况发生在 Config.json 位于当前工作目录，且 Path.GetDirectoryName 返回 null 或 "" 时。
                    // 此时使用当前工作目录。
                    dir = Environment.CurrentDirectory;
                }
            }

            // 确保目录存在
            if (!Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception e)
                {
                    Logger.Fatal($"Failed to create cache directory '{dir}': {e.Message}");
                }
            }

            var fileName = $"{config.Record}.{config.Zone}{CacheFileSuffix}";
            return Path.Combine(dir, fileName);
        }
        public string ReadLastIP(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    return File.ReadLines(path).FirstOrDefault()?.Trim() ?? "";
                }
                catch (Exception e)
                {
                    Logger.Error("Could not read IP cache file {0}: {1}", path, e.Message);
                }
            }
            return "";
        }

        public void WriteLastIP(string path, string ip)
        {
            try
            {
                File.WriteAllText(path, ip + Environment.NewLine);
            }
            catch (Exception e)
            {
                Logger.Error("Could not write IP cache file {0}: {1}", path, e.Message);
            }
        }
    }
}
