using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using MacosUtils.Core;
using MacosUtils.Core.Logging;

// ChatReasoningEffortLevel is marked experimental (OPENAI001) but is the only
// way to send reasoning_effort; scoped suppression for this file.
#pragma warning disable OPENAI001

namespace MacosUtils.AI;

/// <summary>Which reasoning effort to request from the model. Default sends nothing.</summary>
public enum ReasoningEffort
{
    Default,
    Minimal,
    Low,
    Medium,
    High,
}

/// <param name="ResponseTimeout">How long the endpoint may send nothing before a model call
/// fails; null uses <see cref="ChatSession.DefaultResponseTimeout"/>.</param>
public sealed record ChatAgentOptions(
    string Endpoint,
    string Model,
    string ApiKey,
    ReasoningEffort Reasoning = ReasoningEffort.Default,
    string? Instructions = null,
    TimeSpan? ResponseTimeout = null);

/// <summary>What the agent is doing while a reply streams.</summary>
public enum ChatActivity
{
    /// <summary>Waiting for the endpoint to answer (request sent, nothing received yet).</summary>
    Waiting,
    /// <summary>The model is streaming reasoning (reasoning_content) before its answer.</summary>
    Thinking,
    /// <summary>The model asked for a tool; it is running on this Mac.</summary>
    RunningTool,
    /// <summary>The model is streaming answer text.</summary>
    Writing,
}

/// <summary>One streamed item: the current activity, plus answer text to append when <see cref="Text"/> is set.</summary>
public readonly record struct ChatStreamUpdate(ChatActivity Activity, string? Text = null, string? ToolName = null);

/// <summary>A tool call that runs only if the user allows it (see <see cref="ChatSession.StreamResponseAsync"/>).</summary>
/// <param name="ToolName">The tool the model asked for, e.g. ReadTextFile.</param>
/// <param name="Prompt">A ready-to-show question naming what would be read and where it is sent.</param>
public sealed record ToolApprovalRequest(string ToolName, string Prompt);

/// <summary>
/// Owns the <see cref="AIAgent"/> and its conversation session.
/// UI-agnostic: usable from WinForms, console apps, services, etc.
/// </summary>
public sealed class ChatSession
{
    private const string DefaultInstructions =
        "You are a helpful macOS assistant running inside the MacosUtils app. " +
        "Use the provided tools whenever the user asks about this Mac (system, processes, volumes, files). " +
        "All tools are read-only; never claim to change anything. " +
        "Reading a file's text or an environment variable asks the user first; if the user declines, " +
        "do not ask for the same item again, just say you were not allowed. Keep answers concise.";

    /// <summary>Default for <see cref="ChatAgentOptions.ResponseTimeout"/>. Reasoning models stream
    /// their thinking, which counts as data, so only a silent endpoint hits this. Kept below the
    /// OpenAI SDK's 100 s network timeout so this clear error fires before the SDK's own retries.</summary>
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(90);

    private static ILogger Log => AppLog.Logger<ChatSession>();

    private AIAgent _agent;
    private AgentSession? _session;
    private ChatAgentOptions _options;

    private ChatSession(ChatAgentOptions options, AIAgent agent)
    {
        _options = options;
        _agent = agent;
    }

    public static ChatSession Create(ChatAgentOptions options)
    {
        var endpoint = options.Endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("Enter a model name (e.g. gpt-4o-mini).");
        // API keys never contain meaningful surrounding whitespace (often pasted with a stray space/newline).
        var apiKey = options.ApiKey?.Trim() ?? "";
        if (apiKey.Length == 0)
            throw new InvalidOperationException("Enter an API key.");

        var normalized = options with { Endpoint = endpoint, ApiKey = apiKey };
        Log.LogInformation("Creating chat session for {Endpoint} with model {Model}.", endpoint, options.Model);
        return new ChatSession(normalized, BuildAgent(normalized, endpointUri));
    }

    private static AIAgent BuildAgent(ChatAgentOptions options, Uri endpointUri)
    {
        var chatClient = PcTools.CreateChatClient(options.Model, options.ApiKey, endpointUri);
        // Tools that read file contents or environment variable values wait for the user's
        // permission; see StreamResponseAsync. The tool list itself comes from PcTools so the
        // MCP server (McpServerHost) exposes exactly the same capabilities.
        AITool[] tools = [.. PcTools.CreateTools().Select(t =>
            t.NeedsApproval ? (AITool)new ApprovalRequiredAIFunction(t.Function) : t.Function)];
        return chatClient.AsAIAgent(
            options.Instructions ?? DefaultInstructions,
            "macos-utils-assistant",
            "Mac assistant",
            tools,
            BuildClientFactory(options),
            null,
            null);
    }

    // Wraps each model call made by the agent (including the calls inside its tool loop).
    private static Func<IChatClient, IChatClient> BuildClientFactory(ChatAgentOptions options)
    {
        var timeout = options.ResponseTimeout ?? DefaultResponseTimeout;
        var level = ToChatReasoningEffortLevel(options.Reasoning);
        return inner =>
        {
            IChatClient client = new ResponseTimeoutChatClient(inner, timeout);
            if (level is { } effort)
                client = new ReasoningEffortChatClient(client, effort);
            return client;
        };
    }

    // NOTE: do not use a switch expression with a `_ => null` arm here. Roslyn compiles
    // the null arm through the struct's implicit string operator with a null string,
    // which throws ArgumentNullException. Plain `return null` is safe.
    internal static OpenAI.Chat.ChatReasoningEffortLevel? ToChatReasoningEffortLevel(ReasoningEffort reasoning)
    {
        if (reasoning == ReasoningEffort.Minimal)
            return OpenAI.Chat.ChatReasoningEffortLevel.Minimal;
        if (reasoning == ReasoningEffort.Low)
            return OpenAI.Chat.ChatReasoningEffortLevel.Low;
        if (reasoning == ReasoningEffort.Medium)
            return OpenAI.Chat.ChatReasoningEffortLevel.Medium;
        if (reasoning == ReasoningEffort.High)
            return OpenAI.Chat.ChatReasoningEffortLevel.High;
        return null;
    }

    public bool HasReasoningEffort => _options.Reasoning != ReasoningEffort.Default;

    /// <summary>Rebuilds the agent without reasoning_effort and drops the session.</summary>
    public void DisableReasoningEffort()
    {
        if (!HasReasoningEffort)
            return;
        Log.LogWarning("Disabling reasoning_effort: the model rejected it; retrying without it.");
        _options = _options with { Reasoning = ReasoningEffort.Default };
        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpointUri))
            throw new InvalidOperationException("Enter a valid endpoint URL (e.g. https://api.openai.com/v1).");
        _agent = BuildAgent(_options, endpointUri);
        _session = null;
    }

    /// <summary>Drops the conversation session so the next call starts fresh.</summary>
    public void Reset() => _session = null;

    // Note: no try/catch here (yield is not allowed in a try block with a catch
    // clause). Callers must call Reset() after a failed turn: a failed turn can
    // leave the session in an unusable state (e.g. a 400 mid-run).
    // A TimeoutException (silent endpoint) leaves the session intact: the failed turn is
    // not added to the history, so the conversation can simply continue.
    /// <param name="approveToolCall">Asked before each tool call that needs the user's permission
    /// (ReadTextFile, GetEnvironmentVariable); returns true to run it. When null, those calls are declined.</param>
    public async IAsyncEnumerable<ChatStreamUpdate> StreamResponseAsync(
        string prompt,
        Func<ToolApprovalRequest, CancellationToken, Task<bool>>? approveToolCall = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = _session ??= await _agent.CreateSessionAsync(cancellationToken);
        List<Microsoft.Extensions.AI.ChatMessage> input = [new(ChatRole.User, prompt)];
        while (true)
        {
            var approvals = new List<ToolApprovalRequestContent>();
            await foreach (var update in _agent.RunStreamingAsync(input, session, cancellationToken: cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } text:
                            yield return new(ChatActivity.Writing, text.Text);
                            break;
                        case TextReasoningContent:
                            yield return new(ChatActivity.Thinking);
                            break;
                        case FunctionCallContent call:
                            yield return new(ChatActivity.RunningTool, ToolName: call.Name);
                            break;
                        case FunctionResultContent:
                            // The tool finished; its result goes back to the model.
                            yield return new(ChatActivity.Waiting);
                            break;
                        case ToolApprovalRequestContent approval:
                            approvals.Add(approval);
                            break;
                    }
                }
            }
            if (approvals.Count == 0)
                yield break;

            // The run stopped before calling tools that need permission (the agent already runs the
            // other tools of the same batch itself). Send back one answer per request: approved
            // calls then run, declined ones reach the model as rejected.
            var answers = new List<AIContent>(approvals.Count);
            foreach (var approval in approvals)
            {
                var approved = approveToolCall is not null
                    && await approveToolCall(DescribeToolCall(approval.ToolCall), cancellationToken);
                var toolName = approval.ToolCall is FunctionCallContent call ? call.Name : approval.ToolCall.GetType().Name;
                Log.LogInformation("Tool {Tool} approval: {Approved}.", toolName, approved ? "allowed" : "declined");
                answers.Add(approval.CreateResponse(approved, approved ? null : "The user did not allow this tool call."));
            }
            input = [new(ChatRole.User, answers)];
            yield return new(ChatActivity.Waiting);
        }
    }

    // The question shown before a tool call runs, with the AI endpoint as the destination.
    private ToolApprovalRequest DescribeToolCall(ToolCallContent toolCall) =>
        DescribeToolCall(toolCall,
            Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var uri) ? uri.Authority : _options.Endpoint);

    // The path is shown the way ReadTextFile resolves it. destination is where the result
    // goes (the AI endpoint authority, or "the connected MCP client" for the MCP server).
    internal static ToolApprovalRequest DescribeToolCall(ToolCallContent toolCall, string destination)
    {
        if (toolCall is not FunctionCallContent call)
            return new(toolCall.GetType().Name, $"The AI model wants to run a tool. Its result will be sent to {destination}.\n\nAllow?");

        string Argument(string name) =>
            call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) ? value?.ToString() ?? "" : "";
        string ArgOr(string name, string fallback)
        {
            var value = Argument(name);
            return value.Length > 0 ? value : fallback;
        }
        var localPath = PcTools.ToLocalPath(Argument("path"));
        var prompt = call.Name switch
        {
            // The size is shown so an unexpectedly huge file can be declined without reading it.
            nameof(PcTools.ReadTextFile) =>
                $"The AI model wants to read this file ({FileSizeText(localPath)}):\n\n{localPath ?? Argument("path")}\n\n"
                + $"Its text will be sent to {destination}.",
            nameof(PcTools.GetEnvironmentVariable) =>
                $"The AI model wants to read this environment variable:\n\n{Argument("name")}\n\n"
                + $"Its value will be sent to {destination}.",
            nameof(PcTools.ListEnvironmentVariables) =>
                $"The AI model wants to list the {ArgOr("target", "Process")} environment variables.\n\n"
                + $"Their values will be sent to {destination}.",
            // The host name is shown: DNS resolves it, so whatever the model embedded in it leaves
            // this Mac to an outside server — the user should be able to spot that before it happens.
            nameof(PcTools.PingHost) =>
                $"The AI model wants to ping this host:\n\n{ArgOr("host", "?")} ({ArgOr("count", "4")} tries)\n\n"
                + $"Resolving the name and the probes leave this Mac over the network; the result goes to {destination}.",
            nameof(PcTools.TraceTcpRoute) =>
                $"The AI model wants to trace the route to this host over TCP:\n\n{ArgOr("host", "?")} "
                + $"(port {ArgOr("port", "80")}, up to {ArgOr("maxHops", "10")} hops)\n\n"
                + $"Resolving the name and the probes leave this Mac over the network, through its "
                + $"firewall; the result goes to {destination}.",
            _ => $"The AI model wants to run the tool {call.Name}. Its result will be sent to {destination}.",
        };
        return new(call.Name, prompt + "\n\nAllow?");
    }

    // Human-readable size of the file that ReadTextFile is about to be allowed to read.
    private static string FileSizeText(string? path)
    {
        if (path is null)
            return "unknown size";
        try
        {
            return ByteFormatter.FormatBytes(new FileInfo(path).Length);
        }
        catch (Exception)
        {
            return "unknown size";
        }
    }

    public static bool IsReasoningEffortError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when the endpoint rejected the credentials (HTTP 401/403).</summary>
    /// <remarks>Uses the HTTP status code, not the message text: messages often contain
    /// numbers such as token counts ("Requested 4031") that looked like 401/403.</remarks>
    public static bool IsUnauthorizedError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var status = current switch
            {
                ClientResultException clientError => clientError.Status,
                HttpRequestException { StatusCode: { } code } => (int)code,
                _ => 0,
            };
            if (status is 401 or 403)
                return true;
        }
        return false;
    }
}
