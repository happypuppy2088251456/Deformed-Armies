using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Game.Net
{
    /// <summary>
    /// 网络工具：本机 IPv4 探测、地址解析、单调时钟。
    /// 面向“交换机局域网联机”场景，不需要 NAT 穿透。
    /// </summary>
    public static class NetUtil
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>单调递增的毫秒时钟（用于心跳 / 超时判定，不受系统改时间影响）。</summary>
        public static long NowMs => Clock.ElapsedMilliseconds;

        // 虚拟网卡 / 隧道网卡关键词：这些地址不可能是队友能访问到的地址
        private static readonly string[] VirtualKeywords =
        {
            "vmware", "virtualbox", "vbox", "hyper-v", "vethernet", "loopback",
            "tap-", "tap0", "tun", "openvpn", "wireguard", "vpn", "wsl",
            "docker", "bluetooth", "zerotier", "tailscale", "hamachi",
            "radmin", "npcap", "virtual", "loopback adapter", "teredo",
            "isatap", "6to4", "microsoft wi-fi direct", "pseudo"
        };

        private sealed class Candidate
        {
            public string Address;
            public int Score;
        }

        /// <summary>
        /// 取本机最可能被队友访问到的 IPv4（例如 192.168.1.3）。
        /// 优先：有默认网关 &gt; 私有网段 &gt; 有线/无线物理网卡；虚拟网卡会被降权。
        /// </summary>
        public static string GetLocalIPv4()
        {
            List<string> all = GetLocalIPv4List();
            if (all.Count > 0) return all[0];

            // 兜底：DNS 解析主机名
            try
            {
                IPAddress[] addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
                foreach (IPAddress a in addresses)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        return a.ToString();
                }
            }
            catch (Exception e)
            {
                NetLog.Warn("Dns.GetHostEntry 失败: " + e.Message);
            }

            return "127.0.0.1";
        }

        /// <summary>按可信度从高到低返回所有本机 IPv4，可用于 UI 下拉框让玩家手动选网卡。</summary>
        public static List<string> GetLocalIPv4List()
        {
            var candidates = new List<Candidate>();

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    bool virtualNic = IsVirtual(nic);
                    bool hasGateway = false;
                    try
                    {
                        foreach (GatewayIPAddressInformation gw in nic.GetIPProperties().GatewayAddresses)
                        {
                            if (gw != null && gw.Address != null &&
                                gw.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                hasGateway = true;
                                break;
                            }
                        }
                    }
                    catch (Exception) { /* 某些网卡不允许读网关，忽略 */ }

                    try
                    {
                        foreach (UnicastIPAddressInformation uni in
                                 nic.GetIPProperties().UnicastAddresses)
                        {
                            if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            if (IPAddress.IsLoopback(uni.Address)) continue;

                            string ip = uni.Address.ToString();

                            int score = 0;
                            if (hasGateway) score += 100;
                            if (IsPrivateIPv4(ip)) score += 50;

                            switch (nic.NetworkInterfaceType)
                            {
                                case NetworkInterfaceType.Ethernet:
                                case NetworkInterfaceType.GigabitEthernet:
                                case NetworkInterfaceType.FastEthernetT:
                                case NetworkInterfaceType.Ethernet3Megabit:
                                    score += 30;
                                    break;
                                case NetworkInterfaceType.Wireless80211:
                                    score += 25;
                                    break;
                            }

                            if (virtualNic) score -= 200;
                            if (ip.StartsWith("169.254.")) score -= 300; // APIPA，没拿到 DHCP

                            candidates.Add(new Candidate { Address = ip, Score = score });
                        }
                    }
                    catch (Exception) { /* 忽略读不到的网卡 */ }
                }
            }
            catch (Exception e)
            {
                NetLog.Warn("枚举网卡失败: " + e.Message);
            }

            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            var result = new List<string>(candidates.Count);
            foreach (Candidate c in candidates)
            {
                if (!result.Contains(c.Address)) result.Add(c.Address);
            }
            return result;
        }

        private static bool IsVirtual(NetworkInterface nic)
        {
            string name = (nic.Name ?? string.Empty).ToLowerInvariant();
            string desc = (nic.Description ?? string.Empty).ToLowerInvariant();

            foreach (string key in VirtualKeywords)
            {
                if (name.Contains(key) || desc.Contains(key)) return true;
            }
            return false;
        }

        public static bool IsPrivateIPv4(string ip)
        {
            IPAddress addr;
            if (!IPAddress.TryParse(ip, out addr)) return false;

            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) return false;

            if (b[0] == 10) return true;                                  // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                  // 192.168.0.0/16
            return false;
        }

        /// <summary>拼成 "192.168.1.3:10003"。</summary>
        public static string FormatAddress(string ip, int port)
        {
            return string.IsNullOrEmpty(ip) ? "未知地址" : ip + ":" + port;
        }

        /// <summary>
        /// 解析玩家输入的地址。支持 "192.168.1.3"、"192.168.1.3:10003"、
        /// "tcp://192.168.1.3:10003/" 等写法；未写端口时使用 defaultPort。
        /// </summary>
        public static bool TryParseAddress(string text, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;

            if (string.IsNullOrEmpty(text)) return false;

            text = text.Trim();

            int scheme = text.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) text = text.Substring(scheme + 3);

            int slash = text.IndexOf('/');
            if (slash >= 0) text = text.Substring(0, slash);

            int colon = text.LastIndexOf(':');
            if (colon > 0 && colon < text.Length - 1)
            {
                int parsed;
                if (int.TryParse(text.Substring(colon + 1), out parsed) && parsed > 0 && parsed <= 65535)
                {
                    port = parsed;
                    text = text.Substring(0, colon);
                }
            }

            host = text.Trim();
            if (host.Length == 0) return false;

            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return true;

            return Uri.CheckHostName(host) != UriHostNameType.Unknown;
        }

        /// <summary>取本机所有 IPv4 的 IPAddress 对象，用于绑定指定网卡。</summary>
        public static IPAddress ResolveBindAddress(string ipv4)
        {
            if (string.IsNullOrEmpty(ipv4) || ipv4 == "0.0.0.0") return IPAddress.Any;
            IPAddress addr;
            return IPAddress.TryParse(ipv4, out addr) ? addr : IPAddress.Any;
        }
    }
}
