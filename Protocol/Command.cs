using System;

namespace WifiDirectService.Protocol
{
    public class Command
    {
        public string Action { get; set; } = string.Empty;
        public string? Values { get; set; }
        public string Ip { get; set; } = string.Empty;
    }
}