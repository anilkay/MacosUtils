using Microsoft.Extensions.AI;
using OpenAI.Chat;

// ChatReasoningEffortLevel is marked experimental (OPENAI001) but is the only
// way to send reasoning_effort; scoped suppression for this file.
#pragma warning disable OPENAI001

namespace MacosUtils.AI;

/// <summary>Injects reasoning_effort into every request (incl. the agent's
/// internal tool-loop calls). Only reasoning models accept it; "Default" sends nothing.</summary>
internal sealed class ReasoningEffortChatClient(IChatClient inner, ChatReasoningEffortLevel level) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        => base.GetResponseAsync(messages, WithEffort(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        => base.GetStreamingResponseAsync(messages, WithEffort(options), cancellationToken);

    private ChatOptions WithEffort(ChatOptions? options)
    {
        options ??= new();
        options.RawRepresentationFactory ??= _ => new ChatCompletionOptions { ReasoningEffortLevel = level };
        return options;
    }
}
