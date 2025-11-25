// Models/IPv6Info.cs
using System.Net;

namespace CfDdnsClient.Models
{
    public record IPv6Info(
        string IP,
        string Scope,
        string AddressState,
        TimeSpan ValidLft,
        TimeSpan PreferredLft,
        bool IsCandidate,
        bool IsULA,
        bool IsDeprecated
    );
}