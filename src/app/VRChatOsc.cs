using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QFTPlus;

internal static class VRChatOsc
{
    internal const int DefaultPort = 9000;
    internal const string Osc = "_osc._udp.local", Query = "_oscjson._tcp.local";
    const string Client = "VRChat-Client-";
    static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };

    internal static (int? Osc, int? Query) Find(TimeSpan wait)
    {
        int? osc = null, query = null;
        try
        {
            var local = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, Group.Port));
            foreach (var address in local) try { socket.JoinMulticastGroup(Group.Address, address); } catch (SocketException) { }
            socket.Client.ReceiveTimeout = 200;
            void Ask(string name, ushort type)
            {
                var question = new List<byte>(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                foreach (var label in name.Split('.')) question.AddRange([(byte)label.Length, .. Encoding.UTF8.GetBytes(label)]);
                question.AddRange([0, 0, (byte)type, 0, 1]);
                foreach (var address in local)
                    try
                    {
                        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        socket.Send(question.ToArray(), question.Count, Group);
                    }
                    catch (SocketException) { }
            }
            Ask(Osc, 12);
            Ask(Query, 12);
            var asked = new HashSet<string>();
            for (var until = Environment.TickCount64 + (long)wait.TotalMilliseconds; Environment.TickCount64 < until && (osc is null || query is null);)
            {
                IPEndPoint? from = null;
                byte[] message;
                try { message = socket.Receive(ref from); }
                catch (SocketException) { continue; }
                if (!IPAddress.IsLoopback(from.Address) && !local.Contains(from.Address)) continue;
                var read = Read(message);
                (osc, query) = (osc ?? read.Osc, query ?? read.Query);
                foreach (var client in read.Clients) if (asked.Add(client)) Ask(client, 33);
            }
        }
        catch (SocketException) { }
        return (osc, query);
    }

    internal static HashSet<string>? AvatarParameters(int queryPort)
    {
        try
        {
            var names = new HashSet<string>();
            void Walk(JsonNode? node)
            {
                if (node is not JsonObject item) return;
                if (item["TYPE"] is not null && item["FULL_PATH"]?.GetValue<string>() is { } path && path.StartsWith("/avatar/parameters/"))
                    names.Add(path["/avatar/parameters/".Length..]);
                if (item["CONTENTS"] is JsonObject contents) foreach (var (_, child) in contents) Walk(child);
            }
            Walk(JsonNode.Parse(Http.GetStringAsync($"http://127.0.0.1:{queryPort}/avatar").GetAwaiter().GetResult()));
            return names;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    internal static (string[] Clients, int? Osc, int? Query) Read(byte[] message)
    {
        var clients = new List<string>();
        int? osc = null, query = null;
        try
        {
            var at = 12;
            for (var q = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4)); q > 0; q--) { Name(message, ref at); at += 4; }
            var records = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(8)) + BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10));
            for (; records > 0; records--)
            {
                var owner = Name(message, ref at);
                var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(at));
                var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(at + 8));
                var data = at + 10;
                at = data + length;
                if (type == 33 && owner.StartsWith(Client, StringComparison.OrdinalIgnoreCase))
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(data + 4));
                    if (owner.EndsWith("." + Osc, StringComparison.OrdinalIgnoreCase)) osc = port;
                    if (owner.EndsWith("." + Query, StringComparison.OrdinalIgnoreCase)) query = port;
                }
                if (type == 12 && Name(message, ref data) is var target && target.StartsWith(Client, StringComparison.OrdinalIgnoreCase)) clients.Add(target);
            }
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException) { }
        return (clients.ToArray(), osc, query);
    }

    static string Name(byte[] message, ref int at)
    {
        var labels = new List<string>();
        for (int p = at, jumps = 0; ;)
        {
            int length = message[p];
            if (length == 0)
            {
                if (jumps == 0) at = p + 1;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (jumps++ == 0) at = p + 2;
                if (jumps > 16) throw new InvalidDataException();
                p = (length & 0x3F) << 8 | message[p + 1];
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(message, p + 1, length));
            p += length + 1;
        }
    }
}
