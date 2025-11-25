// Program.cs
using CfDdnsClient.Models;
using CfDdnsClient.Services;
using System.Text.Json;

namespace CfDdnsClient;

public class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length < 3 || (args[1] != "-f" && args[1] != "--config"))
        {
            ShowUsage();
            return;
        }

        string command = args[0].ToLowerInvariant();
        string configFilePath = args[2];

        try
        {
            var config = Config.Load(configFilePath);
            var platformService = new PlatformService();
            var cloudflareService = new CloudflareService();
            
            if (!config.Provider.Equals("cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Fatal($"Provider '{config.Provider}' is not supported yet.");
            }

            switch (command)
            {
                case "run":
                    await HandleRunCommand(config, cloudflareService, platformService);
                    break;
                case "show":
                    await HandleShowCommand(config, platformService);
                    break;
                default:
                    ShowUsage();
                    break;
            }
        }
        catch (Exception e)
        {
            // 捕获所有未处理的异常，并以 FATAL 退出
            Logger.Fatal("Unhandled exception: {0}", e.Message);
        }
    }

    private static void ShowUsage()
    {
        Console.WriteLine("Usage: cfddns-client <command> -f/--config <path>");
        Console.WriteLine("\nCommands:");
        Console.WriteLine("  run   - Execute the dynamic DNS update (for cron/systemd).");
        Console.WriteLine("  show  - Display available IPv6 addresses and their status.");
        Environment.Exit(1);
    }

    // --- RUN COMMAND --- (已集成 IP 获取故障转移逻辑)
    private static async Task HandleRunCommand(Config config, CloudflareService cfService, PlatformService pService)
    {
        Logger.Info("Starting DDNS update sequence...");

        string currentIP = "";

        // 1. 优先级 1: 尝试使用本地 'ip' 命令获取 IP
        Logger.Info("Attempting to get local IPv6 from interface '{0}'...", config.InterfaceName);
        try
        {
            var infos = await pService.GetAvailableIPv6(config.InterfaceName);
            currentIP = pService.SelectBestIPv6(config, infos);
        }
        catch (InvalidOperationException e)
        {
            // 捕获 PlatformService 抛出的 '找不到IP' 或 '索引越界' 异常
            Logger.Warning("Local 'ip' command method failed: {0}", e.Message);
        }
        
        // 2. 优先级 2: 故障转移到外部 API
        if (string.IsNullOrEmpty(currentIP) && config.IpApiUrls != null && config.IpApiUrls.Count > 0)
        {
            Logger.Warning("Local IP acquisition failed. Falling back to external APIs.");
            try
            {
                currentIP = await pService.GetExternalIPv6(config.IpApiUrls);
            }
            catch (InvalidOperationException e)
            {
                // 如果外部 API 也失败了，则彻底终止
		Logger.Fatal("External API failover also failed. Aborting update. Error: {0}", e.Message!);
            }
        }
        else if (string.IsNullOrEmpty(currentIP))
        {
            // 如果本地 IP 获取失败，且没有配置外部 API，则彻底终止
            Logger.Fatal("Failed to get any valid IPv6 address and no external APIs configured. Aborting update.");
        }
        
        // 3. 检查 IP 缓存 (.lastip 文件)
        var cacheFilePath = pService.GetCacheFilePath(config);
        var lastIP = pService.ReadLastIP(cacheFilePath);

        if (lastIP != "" && lastIP == currentIP)
        {
            Logger.Info("IP not changed ({0}). Exiting.", currentIP);
            return;
        }

        if (lastIP != "")
        {
            Logger.Info("IP address changed from {0} to {1}. Initiating update.", lastIP, currentIP);
        }
        else
        {
            Logger.Info("No previous IP found. Initiating first-time update to {0}.", currentIP);
        }

        // 4. 获取 Zone ID
        var cloudflareConfig = config.Cloudflare ?? throw new InvalidOperationException("Cloudflare configuration is missing.");

        var zoneID = cloudflareConfig.ZoneId;
        if (string.IsNullOrEmpty(zoneID))
        {
            Logger.Info("Zone ID not cached. Fetching from Cloudflare...");
            var fetchedZoneID = await cfService.ResolveZoneIdAsync(config);

            config = config.UpdateCloudflareZoneId(fetchedZoneID);
            config.Save();
            Logger.Info("New Zone ID saved to config file.");

            zoneID = fetchedZoneID;
        }

        // 5. Upsert DNS Record
        try
        {
            await cfService.UpdateDnsRecordAsync(config, currentIP, zoneID);
            // 6. 更新 IP 缓存
            pService.WriteLastIP(cacheFilePath, currentIP);
            Logger.Success("DDNS update completed. IP: {0}", currentIP);
        }
        catch (Exception e)
        {
            Logger.Error("DDNS update failed for IP {0}. Error: {1}", currentIP, e.Message);
            Environment.Exit(1);
        }
    }

    // --- SHOW COMMAND ---
    private static async Task HandleShowCommand(Config config, PlatformService pService)
    {
        List<IPv6Info> infos;
        try
        {
            infos = await pService.GetAvailableIPv6(config.InterfaceName);
        }
        catch (Exception e)
        {
            Logger.Fatal("Failed to query IP addresses: {0}", e.Message);
            return; // 理论上不会执行到这里
        }
        
        // 过滤掉 Link Local 地址 (fe80::)，只保留 Global Unicast
        var filteredInfos = infos
            .Where(i => i.Scope.Equals("Global Unicast", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var addressList = filteredInfos.Select((info, index) => new
        {
            Index = index + 1, 
            IPAddress = info.IP,
            ScopeType = info.Scope,
            AddressState = info.AddressState,
            PreferredLifetime = pService.FormatTimeSpan(info.PreferredLft),
            ValidLifetime = pService.FormatTimeSpan(info.ValidLft),
            DDNSCandidate = info.IsCandidate,
            CandidateDetails = new
            {
                Global = info.Scope.Equals("Global Unicast", StringComparison.OrdinalIgnoreCase),
                ULA = info.IsULA,
                Deprecated = info.IsDeprecated
            }
        }).ToList();
        
        var outputData = new 
        {
            InterfaceName = config.InterfaceName, 
            Addresses = addressList 
        };

        var jsonOptions = new JsonSerializerOptions { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        
        Console.WriteLine(JsonSerializer.Serialize(outputData, jsonOptions));
    }
}
