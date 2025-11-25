// Services/CloudflareService.cs
using CfDdnsClient.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CfDdnsClient.Services
{
    public class CloudflareService
    {
        private const string ApiBase = "https://api.cloudflare.com/client/v4";
        private const string ZonesEndpoint = ApiBase + "/zones";

        // 全局单例 HttpClient（支持代理 + 超时控制）
        private static HttpClient? _httpClient;
        private static readonly object LockObj = new();

        private HttpClient GetHttpClient(string? proxyUrl = null, int timeoutSeconds = 20)
        {
            if (_httpClient != null)
                return _httpClient;

            lock (LockObj)
            {
                if (_httpClient != null)
                    return _httpClient;

                var handler = new HttpClientHandler();

                if (!string.IsNullOrWhiteSpace(proxyUrl))
                {
                    try
                    {
                        handler.Proxy = new System.Net.WebProxy(proxyUrl);
                        handler.UseProxy = true;
                        Logger.Info("Using proxy for Cloudflare API: {0}", proxyUrl);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("Proxy configuration failed ({0}), falling back to direct connection: {1}", proxyUrl, ex.Message);
                        // 代理配置错误也不崩溃，继续直连
                    }
                }
                else
                {
                    Logger.Info("No proxy configured, using direct connection to Cloudflare API");
                }

                _httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)) // 最小 5 秒
                };
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "CfDdnsClient/3.0 (+https://github.com/yourname/cfddns)");

                return _httpClient;
            }
        }

        private async Task<CfApiResponse<T>> MakeRequestAsync<T>(
            Config config,
            string token,
            string url,
            HttpMethod method,
            object? body = null)
        {
            var client = GetHttpClient(config.Proxy, config.HttpTimeoutSeconds);

            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (body != null)
            {
                var jsonBody = JsonSerializer.Serialize(body, Config.JsonSerializerOptions);
                request.Content = new StringContent(jsonBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            try
            {
                var response = await client.SendAsync(request);
                var responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errorMsg = responseString.Length > 200 ? responseString[..200] + "..." : responseString;
                    throw new HttpRequestException($"Cloudflare API error {response.StatusCode}: {errorMsg}");
                }

                var apiResponse = JsonSerializer.Deserialize<CfApiResponse<T>>(responseString, Config.JsonSerializerOptions);
                if (apiResponse == null || !apiResponse.Success)
                {
                    var msg = apiResponse?.Errors?.FirstOrDefault()?.Message ?? "Unknown error";
                    throw new InvalidOperationException($"Cloudflare API returned success=false: {msg}");
                }

                return apiResponse;
            }
            catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
            {
                Logger.Error("Request timed out after {0} seconds. Check network/proxy settings.", config.HttpTimeoutSeconds);
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error("Network request failed: {0}", ex.Message);
                throw;
            }
        }

        public async Task<string> ResolveZoneIdAsync(Config config)
        {
            var cf = config.Cloudflare ?? throw new InvalidOperationException("Cloudflare config missing");
            var url = $"{ZonesEndpoint}?name={config.Zone}";
            var response = await MakeRequestAsync<List<CfZoneResult>>(config, cf.ApiToken, url, HttpMethod.Get);
            var zoneId = response.Result?.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException($"Zone {config.Zone} not found");
            Logger.Info("Fetched Zone ID: {0}", zoneId);
            return zoneId;
        }

        public async Task UpdateDnsRecordAsync(Config config, string ip, string zoneId)
        {
            var cf = config.Cloudflare ?? throw new InvalidOperationException("Cloudflare config missing");
            var fqdn = $"{config.Record}.{config.Zone}";

            var searchUrl = $"{ZonesEndpoint}/{zoneId}/dns_records?type=AAAA&name={fqdn}";
            var searchResponse = await MakeRequestAsync<List<CfRecordResult>>(config, cf.ApiToken, searchUrl, HttpMethod.Get);
            var existing = searchResponse.Result?.FirstOrDefault();

            if (existing != null &&
                existing.Content == ip &&
                existing.Ttl == config.Ttl &&
                existing.Proxied == cf.Proxied)
            {
                Logger.Info("DNS record already up-to-date");
                return;
            }

            var body = new DnsRecordBody("AAAA", fqdn, ip, config.Ttl, cf.Proxied);
            string url;
            HttpMethod method;

            if (existing != null)
            {
                method = HttpMethod.Put;
                url = $"{ZonesEndpoint}/{zoneId}/dns_records/{existing.Id}";
                Logger.Info("Updating existing record (ID: {0})", existing.Id);
            }
            else
            {
                method = HttpMethod.Post;
                url = $"{ZonesEndpoint}/{zoneId}/dns_records";
                Logger.Info("Creating new DNS record");
            }

            await MakeRequestAsync<CfRecordResult>(config, cf.ApiToken, url, method, body);
            Logger.Success("Successfully updated DNS record → {0}", ip);
        }
    }

    // 模型保持不变
    #region Cloudflare API Models
    public record CfZoneResult([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string Name);
    public record CfRecordResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("proxied")] bool Proxied,
        [property: JsonPropertyName("type")] string Type);
    public record DnsRecordBody(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("ttl")] int Ttl,
        [property: JsonPropertyName("proxied")] bool Proxied);
    public record CfError([property: JsonPropertyName("code")] int Code, [property: JsonPropertyName("message")] string Message);
    public record CfApiResponse<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("errors")] List<CfError>? Errors,
        [property: JsonPropertyName("result")] T? Result);
    #endregion
}
