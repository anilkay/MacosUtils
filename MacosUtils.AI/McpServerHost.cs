using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MacosUtils.Core.Logging;

namespace MacosUtils.AI;

/// <summary>Settings for the locally hosted MCP server.</summary>
/// <param name="Agent">Endpoint, model and key used by the agent exposed as the ask_macos_utils_agent tool.</param>
/// <param name="ApproveToolCall">Asked before a tool reads a file's text or an environment variable;
/// when null those calls are declined. Runs on a background (Kestrel) thread, so a UI callback must marshal itself.</param>
/// <param name="Port">The port to listen on. The server refuses to start when it is busy — it never
/// silently moves to another port, so an MCP client configured for this port cannot be fooled.</param>
public sealed record McpServerHostOptions(
    ChatAgentOptions Agent,
    Func<ToolApprovalRequest, CancellationToken, Task<bool>>? ApproveToolCall = null,
    int Port = McpServerHost.DefaultPort);

/// <summary>
/// Hosts a local Model Context Protocol server (streamable HTTP) that exposes the MacosUtils AI
/// agent as the ask_macos_utils_agent tool and the read-only PcTools as native MCP tools, so any
/// MCP client on this Mac (VS Code, Claude Desktop, another agent) can use them. Started/stopped by
/// the AI Chat screen. Binds loopback only, requires a per-start bearer token (<see cref="Token"/>,
/// shown with a ready mcp.json snippet), and rejects requests whose Host or Origin header is not
/// this server's own (DNS-rebinding guard). Other local processes can still call it while it runs,
/// but only with the token.
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    public const int DefaultPort = 5199;
    private const string Path = "/mcp";

    private static ILogger Log => AppLog.Logger<McpServerHost>();

    private readonly McpServerHostOptions _options;
    private readonly ChatSession _agent;
    // New secret per start: MCP clients must be reconfigured after each restart anyway, and a
    // token that outlives the process would be readable from %AppData% by any local process.
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    // The agent owns one conversation session; two MCP clients (or two parallel calls) must take turns.
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private WebApplication? _app;
    private bool _stopped;

    /// <summary>The URL an MCP client connects to, e.g. http://localhost:5199/mcp.</summary>
    public string Url { get; private set; } = "";

    /// <summary>The bearer token requests must carry (Authorization: Bearer). New on every start.</summary>
    public string Token => _token;

    /// <summary>A ready-to-paste mcp.json entry for HTTP-based MCP clients (e.g. VS Code).</summary>
    public string ClientConfigJson =>
        $$"""
        {
          "servers": {
            "macosutils": {
              "type": "http",
              "url": "{{Url}}",
              "headers": { "Authorization": "Bearer {{_token}}" }
            }
          }
        }
        """;

    private McpServerHost(McpServerHostOptions options, ChatSession agent)
    {
        _options = options;
        _agent = agent;
    }

    /// <summary>Starts the server on <see cref="McpServerHostOptions.Port"/>.</summary>
    public static async Task<McpServerHost> StartAsync(McpServerHostOptions options, CancellationToken cancellationToken = default)
    {
        // Rejects incomplete endpoint/model/key settings before Kestrel starts, so the caller can show the message.
        var host = new McpServerHost(options, ChatSession.Create(options.Agent));
        try
        {
            await host.StartCoreAsync(cancellationToken);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // the app has no console to log to
        builder.Logging.AddProvider(new FileLoggerProvider(AppLog.LogDirectory));
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "macosutils", Title = "MacosUtils", Version = "1.0" };
                o.ServerInstructions =
                    "Read-only tools that inspect this Mac: system, volumes, processes, network, firewall, listening ports, "
                    + "login items and launch agents, local users, folders, files and hashes. ask_macos_utils_agent is an AI "
                    + "assistant that answers questions about this Mac with the same tools. Nothing is ever changed. "
                    + "Reading a file's text or an environment variable value asks the desktop user for permission first.";
            })
            .WithHttpTransport()
            .WithTools(CreateTools());
        builder.WebHost.UseUrls($"http://localhost:{_options.Port}");
        var app = builder.Build();
        // Request guard, checked before any MCP handling:
        // - Host must be this server's own loopback name:port (DNS rebinding rewrites Host).
        // - Origin, when a browser sends one, must be this server's own; MCP clients send none.
        // - Authorization must carry this start's bearer token; wrong or missing gets 401.
        var serverPort = _options.Port;
        var token = _token;
        app.Use(async (context, next) =>
        {
            var headers = context.Request.Headers;
            if (!IsThisServerHost(headers.Host.ToString(), serverPort)
                || (headers.Origin.Count > 0 && !IsThisServerOrigin(headers.Origin.ToString(), serverPort)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            // The auth scheme is case-insensitive (RFC 7235); the token itself is compared in constant time.
            var auth = headers.Authorization.ToString();
            var presented = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : "";
            if (!FixedTimeEquals(presented, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
        app.MapMcp(Path);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            Log.LogError(ex, "Could not start the MCP server on port {Port}.", _options.Port);
            throw new InvalidOperationException(
                $"Could not start the MCP server on port {_options.Port}: it is probably already in use by another "
                + $"program. Free the port or change it. ({ex.Message})", ex);
        }
        _app = app;
        Url = $"http://localhost:{serverPort}{Path}";
        Log.LogInformation("MCP server listening on {Url}.", Url);
    }

    // Only the server's own host names are accepted. Browsers put the host they think they are
    // talking to into Host; non-browser MCP clients put the address they connect to, which matches.
    private static bool IsThisServerHost(string host, int port) =>
        host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
        || host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
        || host.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase);

    // Only the server's own loopback origins are accepted (browsers use the scheme+host+port
    // form). Non-browser MCP clients send no Origin at all and skip this check.
    private static bool IsThisServerOrigin(string origin, int port) =>
        origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase)
        || origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
        || origin.Equals($"http://[::1]:{port}", StringComparison.OrdinalIgnoreCase);

    // Token comparison must not leak through timing; string == short-circuits on the first
    // differing character.
    private static bool FixedTimeEquals(string actual, string expected)
    {
        var a = Encoding.UTF8.GetBytes(actual);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private IReadOnlyList<McpServerTool> CreateTools()
    {
        var tools = new List<McpServerTool> { CreateAgentTool() };
        foreach (var (function, needsApproval, openWorld) in PcTools.CreateTools())
        {
            // The private-data tools keep their "ask the user first" rule on this path too.
            var exposed = needsApproval ? new ApprovalGateFunction(function, _options.ApproveToolCall) : function;
            tools.Add(McpServerTool.Create(exposed, new McpServerToolCreateOptions
            {
                // Destructive defaults to true in the MCP spec when the hint is absent.
                ReadOnly = true,
                Destructive = false,
                OpenWorld = openWorld,
            }));
        }
        return tools;
    }

    private McpServerTool CreateAgentTool()
    {
        var function = PcTools.CreateFunction(
            (string request, CancellationToken cancellationToken) => AskAgentAsync(request, cancellationToken),
            "ask_macos_utils_agent",
            "Asks the MacosUtils AI assistant a question about this Mac. It can combine the other tools "
            + "(system info, drives, folders, files, network) and answers in natural language; use it for questions "
            + "that need judgement or several data points. Read-only; may ask the desktop user to allow reading a file.");
        return McpServerTool.Create(function, new McpServerToolCreateOptions
        {
            ReadOnly = true,
            Destructive = false,
            OpenWorld = true, // every call goes out to the configured AI endpoint
        });
    }

    private async Task<string> AskAgentAsync(string request, CancellationToken cancellationToken)
    {
        await _agentLock.WaitAsync(cancellationToken);
        try
        {
            // Each MCP call is a fresh question: the MCP client owns the conversation, not the server.
            _agent.Reset();
            try
            {
                return await RunAgentAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ChatSession.IsReasoningEffortError(ex) && _agent.HasReasoningEffort)
            {
                // The model rejects reasoning_effort: drop it and retry once, like the chat screen does.
                _agent.DisableReasoningEffort();
                _agent.Reset();
                return await RunAgentAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _agent.Reset(); // a failed turn can leave the session in an unusable state
                Log.LogError(ex, "MCP agent call failed.");
                return $"Error: {ex.Message}";
            }
        }
        finally
        {
            _agentLock.Release();
        }
    }

    private async Task<string> RunAgentAsync(string request, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await foreach (var update in _agent.StreamResponseAsync(request, _options.ApproveToolCall, cancellationToken))
        {
            if (update.Text is not null)
                text.Append(update.Text);
        }
        return text.Length > 0 ? text.ToString() : "(the agent returned no text)";
    }

    /// <summary>Stops the server and releases the port. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;
        var app = _app;
        _app = null;
        if (app is null)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await app.StopAsync(timeout.Token);
        }
        catch
        {
            // In-flight requests are cut off when the app closes anyway.
        }
        await app.DisposeAsync();
        Log.LogInformation("MCP server stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // Wraps a private-data tool (ReadTextFile, GetEnvironmentVariable) so the desktop user is asked
    // before each MCP call, matching chat where these are ApprovalRequiredAIFunction.
    private sealed class ApprovalGateFunction(AIFunction inner, Func<ToolApprovalRequest, CancellationToken, Task<bool>>? approve)
        : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var call = new FunctionCallContent("mcp", Name, arguments);
            var request = ChatSession.DescribeToolCall(call, "the connected MCP client");
            if (approve is null || !await approve(request, cancellationToken))
                return "The user did not allow this tool call.";
            return await InnerFunction.InvokeAsync(arguments, cancellationToken);
        }
    }
}
