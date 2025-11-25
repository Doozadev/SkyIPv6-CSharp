// Models/CloudflareModels.cs

namespace CfDdnsClient.Models
{
    // API 响应的错误模型
    public record CfError(int Code, string Message);

    // 泛型 API 响应模型
    public record CfApiResponse<T>(
        bool Success,
        T? Result,
        List<CfError>? Errors
    );

    // 区域查找结果模型
    public record CfZoneResult(string Id, string Name);
    
    // DNS 记录结果模型 (用于获取现有记录)
    public record CfRecordResult(
        string Id,
        string Content,
        string Name,
        int Ttl,
        bool Proxied
    );
    
    // DNS 更新请求体
    public record DnsRecordBody(
        string Type,
        string Name,
        string Content,
        int Ttl,
        bool Proxied
    );
}
