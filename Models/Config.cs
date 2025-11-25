// Models/Config.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using CfDdnsClient.Services;

namespace CfDdnsClient.Models
{
    public record CloudflareConfig(
        [property: JsonPropertyName("apiToken")] string ApiToken,
        [property: JsonPropertyName("zoneId")] string? ZoneId,
        [property: JsonPropertyName("proxied")] bool Proxied = false
    );

    public record Config(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("zoneName")] string Zone,
        [property: JsonPropertyName("recordName")] string Record,
        [property: JsonPropertyName("interfaceName")] string InterfaceName,
        [property: JsonPropertyName("ttl")] int Ttl = 180,
        [property: JsonPropertyName("selectIndex")] int SelectIndex = 0,
        [property: JsonPropertyName("workDir")] string? WorkDir = "",
        [property: JsonPropertyName("ipApiUrls")] List<string>? IpApiUrls = null,
        [property: JsonPropertyName("cloudflare")] CloudflareConfig? Cloudflare = null,

        // 正确的写法：positional record 结束后，再用普通属性声明带 init 的字段
        [property: JsonPropertyName("proxy")] string? Proxy = null,
        [property: JsonPropertyName("httpTimeoutSeconds")] int HttpTimeoutSeconds = 20
    )
    {
        [JsonIgnore]
        public string? ConfigFilePath { get; init; }

        public static readonly JsonSerializerOptions JsonSerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public static Config Load(string path)
        {
            var absolutePath = Path.GetFullPath(path);
            if (!File.Exists(absolutePath))
                Logger.Fatal($"Config file not found: {absolutePath}");

            var jsonString = File.ReadAllText(absolutePath);
            var config = JsonSerializer.Deserialize<Config>(jsonString, JsonSerializerOptions)
                         ?? throw new InvalidOperationException("Failed to deserialize configuration.");

            if (string.IsNullOrEmpty(config.Provider) || string.IsNullOrEmpty(config.Zone) ||
                string.IsNullOrEmpty(config.Record) || string.IsNullOrEmpty(config.InterfaceName))
                Logger.Fatal("Config file missing required standard fields.");

            if (config.Provider.Equals("cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                if (config.Cloudflare == null || string.IsNullOrEmpty(config.Cloudflare.ApiToken))
                    Logger.Fatal("Provider 'cloudflare' requires 'cloudflare.apiToken'.");
            }

            config = config with
            {
                Ttl = config.Ttl > 0 ? config.Ttl : 180,
                SelectIndex = config.SelectIndex >= 0 ? config.SelectIndex : 0,
                WorkDir = config.WorkDir ?? "",
                IpApiUrls = config.IpApiUrls ?? new List<string>()
            };

            config = config with { ConfigFilePath = absolutePath };
            config.Save();
            return config;
        }

        public void Save()
        {
            if (ConfigFilePath == null)
            {
                Logger.Error("Cannot save config: file path is missing.");
                return;
            }
            var jsonString = JsonSerializer.Serialize(this, JsonSerializerOptions);
            File.WriteAllText(ConfigFilePath, jsonString);
        }

        public Config UpdateCloudflareZoneId(string newZoneId)
        {
            if (Cloudflare == null) return this;
            var newCfConfig = Cloudflare with { ZoneId = newZoneId };
            return this with { Cloudflare = newCfConfig };
        }
    }
}
