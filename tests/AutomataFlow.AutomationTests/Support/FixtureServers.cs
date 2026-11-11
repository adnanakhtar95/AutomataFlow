using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AutomataFlow.AutomationTests.Support;

/// <summary>
/// Local servers for the SSRF test matrix:
/// - <see cref="InternalHits"/>: an "internal service" on loopback that must NEVER be reached.
/// - Public site: attack pages, reached only through fake public IPs (8.8.8.8 / 8.8.4.4) mapped by the connector.
/// - A UDP counter for WebRTC leak tests.
/// </summary>
public sealed class FixtureServers : IAsyncDisposable
{
    private WebApplication? _internal;
    private WebApplication? _public;
    private UdpClient? _udp;
    private int _udpPackets;

    public ConcurrentQueue<string> InternalHits { get; } = new();

    public ConcurrentQueue<string> PublicHits { get; } = new();

    public int InternalPort { get; private set; }

    public int PublicPort { get; private set; }

    public int UdpPort { get; private set; }

    public IPAddress LanAddress { get; private set; } = IPAddress.Loopback;

    public int UdpPackets => Volatile.Read(ref _udpPackets);

    public string InternalBase => $"http://127.0.0.1:{InternalPort}";

    public async Task StartAsync()
    {
        _internal = await StartAsync(app => app.Run(async ctx =>
        {
            InternalHits.Enqueue($"{ctx.Request.Method} {ctx.Request.Path}");
            await ctx.Response.WriteAsync("INTERNAL-SECRET");
        }));
        InternalPort = Port(_internal);

        _public = await StartAsync(ConfigurePublicSite);
        PublicPort = Port(_public);

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        UdpPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await _udp.ReceiveAsync();
                    Interlocked.Increment(ref _udpPackets);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        });

        LanAddress = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) ?? IPAddress.Loopback;
    }

    public async ValueTask DisposeAsync()
    {
        _udp?.Dispose();
        if (_internal is not null)
        {
            await _internal.DisposeAsync();
        }

        if (_public is not null)
        {
            await _public.DisposeAsync();
        }
    }

    private void ConfigurePublicSite(WebApplication app)
    {
        static string Html(string body) => $"<!doctype html><html><head><title>fixture</title></head><body>{body}</body></html>";
        static string Q(HttpContext ctx, string key) => WebUtility.HtmlEncode(ctx.Request.Query[key].ToString());

        app.Run(async ctx =>
        {
            PublicHits.Enqueue($"{ctx.Request.Method} {ctx.Request.Host}{ctx.Request.Path}");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            switch (ctx.Request.Path.Value)
            {
                case "/":
                case "/landing":
                case "/query":
                    await ctx.Response.WriteAsync(Html("<h1 data-testid='ok'>public</h1>"));
                    break;
                case "/headers":
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.WriteAsync(string.Join("\n", ctx.Request.Headers.Select(h => $"{h.Key}: {h.Value}")));
                    break;
                case "/redirect":
                    ctx.Response.Redirect(ctx.Request.Query["to"].ToString());
                    break;
                case "/embeds":
                    var b = Q(ctx, "base");
                    await ctx.Response.WriteAsync(Html(
                        $"<h1 data-testid='ok'>embeds</h1><iframe src='{b}/frame'></iframe><img src='{b}/img.png'>" +
                        $"<script>fetch('{b}/api').catch(()=>{{}});</script>" +
                        $"<a data-testid='popup-link' target='_blank' href='{b}/popup'>popup</a>"));
                    break;
                case "/ws":
                    await ctx.Response.WriteAsync(Html($"<h1 data-testid='ok'>ws</h1><script>try{{new WebSocket('{Q(ctx, "url")}')}}catch(e){{}}</script>"));
                    break;
                case "/rebind":
                    await ctx.Response.WriteAsync(Html("<h1 data-testid='ok'>rebind</h1><script>window.addEventListener('load',()=>fetch('/again').catch(()=>{}));</script>"));
                    break;
                case "/self":
                    await ctx.Response.WriteAsync(Html($"<h1 data-testid='ok'>self</h1><script>fetch('http://127.0.0.1:{Q(ctx, "port")}/').catch(()=>{{}});</script>"));
                    break;
                case "/webrtc":
                    await ctx.Response.WriteAsync(Html(
                        $"<h1 data-testid='ok'>webrtc</h1><script>const pc=new RTCPeerConnection({{iceServers:[{{urls:'stun:{Q(ctx, "ip")}:{Q(ctx, "port")}'}}]}});" +
                        "pc.createDataChannel('x');pc.createOffer().then(o=>pc.setLocalDescription(o));</script>"));
                    break;
                case "/alert":
                    await ctx.Response.WriteAsync(Html("<script>alert('fixture dialog');</script><h1 data-testid='ok'>alert</h1>"));
                    break;
                case "/popups":
                    await ctx.Response.WriteAsync(Html("<h1 data-testid='ok'>popups</h1>" + string.Concat(Enumerable.Range(1, 8).Select(i => $"<a data-testid='pop-{i}' target='_blank' href='/landing?p={i}'>pop {i}</a> "))));
                    break;
                case "/downloads":
                    await ctx.Response.WriteAsync(Html(
                        "<a data-testid='dl-csv' href='/download/report.csv'>csv</a> <a data-testid='dl-exe' href='/download/tool.exe'>exe</a> " +
                        "<a data-testid='dl-big' href='/download/big.bin'>big</a> <a data-testid='dl-traversal' href='/download/traversal'>traversal</a>"));
                    break;
                case "/download/report.csv":
                    await Attachment(ctx, "report.csv", Encoding.UTF8.GetBytes("id,name\n1,Fictional Person\n"));
                    break;
                case "/download/tool.exe":
                    await Attachment(ctx, "tool.exe", "MZ-fake-executable"u8.ToArray());
                    break;
                case "/download/big.bin":
                    await Attachment(ctx, "big.bin", new byte[2 * 1024 * 1024]);
                    break;
                case "/download/traversal":
                    await Attachment(ctx, "../../../etc/cron.d/evil.csv", "a,b\n"u8.ToArray());
                    break;
                case "/hang":
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                    break;
                case "/late":
                    await ctx.Response.WriteAsync(Html("<script>setTimeout(()=>{const d=document.createElement('div');d.dataset.testid='late';d.textContent='arrived';document.body.appendChild(d);},400);</script>"));
                    break;
                case "/dup":
                    await ctx.Response.WriteAsync(Html("<button data-testid='twin'>one</button><button data-testid='twin'>two</button>"));
                    break;
                case "/form":
                    await ctx.Response.WriteAsync(Html(
                        "<div style='font-size:20px;padding:10px'>" +
                        "<input data-testid='username' style='display:block;width:300px;height:40px;margin:10px'>" +
                        "<input data-testid='password' type='password' style='display:block;width:300px;height:40px;margin:10px'>" +
                        "<input data-testid='card' style='display:block;width:300px;height:40px;margin:10px'>" +
                        "<select data-testid='color'><option value='r'>Red</option><option value='g'>Green</option></select>" +
                        "<div data-testid='secret-text' data-af-sensitive style='width:300px;height:40px;background:#cfc;margin:10px'>visible-secret-text</div>" +
                        "<div data-testid='plain-text' style='width:300px;height:40px;background:#ccf;margin:10px'>plain</div>" +
                        "<div data-testid='echo'></div>" +
                        "<script>document.querySelector('[data-testid=color]').addEventListener('change',e=>document.querySelector('[data-testid=echo]').textContent=e.target.value);</script></div>"));
                    break;
                case "/storage-set":
                    ctx.Response.Headers.Append("Set-Cookie", "af_test=leak; Path=/");
                    await ctx.Response.WriteAsync(Html("<h1 data-testid='ok'>set</h1><script>localStorage.setItem('af','leak');</script>"));
                    break;
                case "/storage-get":
                    await ctx.Response.WriteAsync(Html("<pre data-testid='storage'></pre><script>document.querySelector('pre').textContent='cookie=['+document.cookie+'] local=['+localStorage.getItem('af')+']';</script>"));
                    break;
                case "/sw":
                    await ctx.Response.WriteAsync(Html("<pre data-testid='sw'>pending</pre><script>const o=document.querySelector('pre');if(!('serviceWorker' in navigator)){o.textContent='unavailable'}else{navigator.serviceWorker.register('/sw.js').then(()=>o.textContent='registered').catch(()=>o.textContent='failed')}</script>"));
                    break;
                case "/sw.js":
                    ctx.Response.ContentType = "text/javascript";
                    await ctx.Response.WriteAsync("self.addEventListener('fetch',()=>{});");
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    break;
            }
        });
    }

    private static async Task Attachment(HttpContext ctx, string fileName, byte[] content)
    {
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        ctx.Response.ContentLength = content.Length;
        await ctx.Response.Body.WriteAsync(content);
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> configure)
    {
        // Fixture servers must not pick up any appsettings.json that lands in the test output (DemoCrm's build
        // output is copied alongside it and restricts AllowedHosts); the fake public hosts must be accepted.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = Path.GetTempPath() });
        builder.Configuration["AllowedHosts"] = "*";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        return app;
    }

    private static int Port(WebApplication app) => new Uri(app.Urls.First()).Port;
}
