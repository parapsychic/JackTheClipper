// Note store + LAN peer-to-peer sync. No WPF in here, so test/ can run it headless.
// Sync model: every peer broadcasts a UDP beacon; on seeing a peer whose data changed, pull its public notes
// over TCP and merge last-writer-wins by Updated. Peers re-serve what they pulled, so notes travel to other networks.
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace JackTheClipper;

public record Note
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";          // plain text; each Store.ImageMark is where the next image goes
    public List<string> Images { get; set; } = [];  // base64 PNGs, in order
    public bool Pinned { get; set; }
    public bool Public { get; set; }
    public bool Shared { get; set; }                // was public at some point, so peers must hear about deletes/unpublishes
    public bool Deleted { get; set; }
    public long Created { get; set; }
    public long Updated { get; set; }               // unix ms
}

public sealed class Store
{
    public const string ImageMark = "\uFFFC";
    public const long Day = 86_400_000, Ttl = 30 * Day; // unpinned notes vanish 30 days after their last edit
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static bool Alive(Note n, long now) => (n.Pinned && !n.Deleted) || now - n.Updated < Ttl;

    // Peers are untrusted: shape-check everything, and cap future timestamps so nobody wins LWW forever.
    static bool Valid([NotNullWhen(true)] Note? r, long now) =>
        r is { Id.Length: > 0 and <= 64 } && r.Updated < now + Day &&
        (r.Deleted || (r is { Title.Length: <= 1000, Text.Length: <= 10_000_000, Images.Count: <= 100 } && r.Images.All(i => i is { Length: <= 50_000_000 })));

    readonly string file;
    readonly Dictionary<string, Note> notes = [];  // stored notes are never mutated, only replaced, so readers can keep references
    readonly Timer saveTimer;
    long rev = Now();
    bool dirty;
    public event Action? Changed;

    public Store(string file)
    {
        this.file = file;
        saveTimer = new Timer(_ => Flush());
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file)) return;
        try { foreach (var n in JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(file), Json) ?? []) notes[n.Id] = n; }
        catch (JsonException) { File.Move(file, $"{file}.corrupt-{Now()}"); } // keep it, never overwrite it
    }

    public long Rev => Interlocked.Read(ref rev);

    void Touch() { Interlocked.Increment(ref rev); dirty = true; saveTimer.Change(500, Timeout.Infinite); }

    // Local edits must beat whatever version we've seen, even if that peer's clock runs ahead.
    static long Stamp(Note? old) => Math.Max(Now(), (old?.Updated ?? 0) + 1);

    public void Flush()
    {
        lock (notes)
        {
            if (!dirty) return;
            try
            {
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(notes.Values, Json));
                File.Move(file + ".tmp", file, true);
                dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("save failed, will retry on next change: " + e.Message);
            }
        }
    }

    public List<Note> List()
    {
        var now = Now();
        lock (notes) return notes.Values.Where(n => !n.Deleted && Alive(n, now)).ToList();
    }

    public void Put(Note n)
    {
        lock (notes)
        {
            notes.TryGetValue(n.Id, out var old);
            notes[n.Id] = n with { Shared = n.Public || old?.Shared == true, Deleted = false, Created = old?.Created ?? Now(), Updated = Stamp(old) };
            Touch();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (notes)
        {
            if (!notes.TryGetValue(id, out var old)) return;
            if (old.Shared) notes[id] = new Note { Id = id, Deleted = true, Shared = true, Updated = Stamp(old) }; // tombstone so peers drop it too
            else notes.Remove(id);
            Touch();
        }
        Changed?.Invoke();
    }

    // What peers see: public notes, plus tombstones for anything that was public and got deleted or made private.
    public List<Note> Shared()
    {
        lock (notes)
            return notes.Values.Where(n => n.Shared)
                .Select(n => n.Public && !n.Deleted ? n : new Note { Id = n.Id, Deleted = true, Updated = n.Updated }).ToList();
    }

    public void Merge(IEnumerable<Note?>? incoming)
    {
        var now = Now();
        var changed = false;
        lock (notes)
        {
            foreach (var r in incoming ?? [])
            {
                if (!Valid(r, now) || !Alive(r, now) || (notes.TryGetValue(r.Id, out var l) && l.Updated >= r.Updated)) continue;
                notes[r.Id] = r.Deleted
                    ? new Note { Id = r.Id, Deleted = true, Shared = true, Updated = r.Updated }
                    : r with { Public = true, Shared = true, Created = r.Created > 0 ? r.Created : r.Updated };
                changed = true;
            }
            if (changed) Touch();
        }
        if (changed) Changed?.Invoke();
    }

    // ponytail: tombstones expire with everything else, so a peer offline >30 days holding a *pinned* copy can resurrect it.
    public void Sweep(long? at = null)
    {
        var now = at ?? Now();
        int count;
        lock (notes)
        {
            var dead = notes.Values.Where(n => !Alive(n, now)).Select(n => n.Id).ToList();
            dead.ForEach(id => notes.Remove(id));
            count = dead.Count;
            if (count > 0) Touch();
        }
        if (count > 0) Changed?.Invoke();
    }
}

public sealed class Sync : IDisposable
{
    const int DiscoveryPort = 47300, BeaconMs = 3000;
    const long MaxPull = 200_000_000; // bytes; refuse bigger dumps from a peer

    record Beacon(string App, string Id, int Port, long Rev);
    sealed class Peer { public long At, Rev = -1; public int Busy; }

    readonly Store store;
    readonly string id = Guid.NewGuid().ToString();
    readonly TcpListener server = new(IPAddress.Any, 0);
    readonly UdpClient udp = new();
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, Peer> peers = new();

    public Sync(Store store)
    {
        this.store = store;
        server.Start();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        udp.EnableBroadcast = true;
        _ = Serve();
        _ = Listen();
        _ = Announce();
    }

    public int Peers => peers.Values.Count(p => Store.Now() - p.At < 3 * BeaconMs);

    public void Dispose() { stop.Cancel(); server.Stop(); udp.Dispose(); }

    async Task Serve()
    {
        while (true)
        {
            TcpClient c;
            try { c = await server.AcceptTcpClientAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                using (c)
                    try
                    {
                        using var t = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        t.CancelAfter(60_000);
                        await c.GetStream().WriteAsync(JsonSerializer.SerializeToUtf8Bytes(store.Shared(), Store.Json), t.Token);
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException) { } // peer went away
            });
        }
    }

    async Task Listen()
    {
        while (true)
        {
            UdpReceiveResult r;
            Beacon? b;
            try
            {
                r = await udp.ReceiveAsync(stop.Token);
                b = JsonSerializer.Deserialize<Beacon>(r.Buffer, Store.Json);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is SocketException or JsonException) { continue; }
            if (b is not { App: "jtc", Id: not null, Port: > 0 and < 65536 } || b.Id == id) continue;

            var p = peers.GetOrAdd(b.Id, _ => new Peer());
            p.At = Store.Now();
            if (p.Rev == b.Rev || Interlocked.Exchange(ref p.Busy, 1) == 1) continue;
            var from = r.RemoteEndPoint.Address;
            _ = Task.Run(async () =>
            {
                try { store.Merge(await Pull(from, b.Port)); p.Rev = b.Rev; }
                catch (Exception e) { Console.Error.WriteLine($"pull {from}: {e.Message}"); }
                finally { p.Busy = 0; }
            });
        }
    }

    // ponytail: full dump per pull (only when the peer's rev changed); add deltas if stores get huge.
    async Task<List<Note?>?> Pull(IPAddress host, int port)
    {
        using var c = new TcpClient();
        using var t = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        t.CancelAfter(10_000);
        await c.ConnectAsync(host, port, t.Token);
        var s = c.GetStream();
        using var buf = new MemoryStream();
        var chunk = new byte[1 << 16];
        int n;
        while ((n = await s.ReadAsync(chunk, t.Token)) > 0)
        {
            if (buf.Length + n > MaxPull) throw new InvalidDataException("too big");
            buf.Write(chunk, 0, n);
            t.CancelAfter(10_000); // idle timeout, not a total one
        }
        return JsonSerializer.Deserialize<List<Note?>>(buf.GetBuffer().AsSpan(0, (int)buf.Length), Store.Json);
    }

    async Task Announce()
    {
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(BeaconMs));
        try
        {
            do
            {
                var msg = JsonSerializer.SerializeToUtf8Bytes(new Beacon("jtc", id, ((IPEndPoint)server.LocalEndpoint).Port, store.Rev), Store.Json);
                foreach (var a in BroadcastAddrs())
                    try { await udp.SendAsync(msg, new IPEndPoint(a, DiscoveryPort), stop.Token); }
                    catch (SocketException) { } // interface went away
            } while (await tick.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) { }
    }

    static List<IPAddress> BroadcastAddrs()
    {
        var list = NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u =>
            {
                var ip = u.Address.GetAddressBytes();
                var mask = u.IPv4Mask.GetAddressBytes();
                for (var k = 0; k < 4; k++) ip[k] |= (byte)~mask[k];
                return new IPAddress(ip);
            })
            .ToList();
        return list.Count > 0 ? list : [IPAddress.Broadcast];
    }
}
