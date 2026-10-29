using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MacosUtils.AI;

/// <summary>
/// Fails a model call with <see cref="TimeoutException"/> when the endpoint sends nothing for
/// <paramref name="timeout"/> (no first chunk, or a stall mid-stream). Without it a silent server
/// leaves the chat on "Thinking..." for minutes while the SDK waits and retries.
/// Sits under the agent's tool-calling loop, so time spent running tools is not counted.
/// </summary>
internal sealed class ResponseTimeoutChatClient(IChatClient inner, TimeSpan timeout) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await base.GetResponseAsync(messages, options, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException();
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var updates = base.GetStreamingResponseAsync(messages, options, cts.Token).GetAsyncEnumerator(cts.Token);
        while (true)
        {
            // The timer only runs while waiting for the endpoint, not while the caller handles an update.
            cts.CancelAfter(timeout);
            bool hasNext;
            try
            {
                hasNext = await updates.MoveNextAsync();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Also covers the SDK's own network timeout, which would otherwise look like a user cancel.
                throw CreateTimeoutException();
            }
            cts.CancelAfter(Timeout.InfiniteTimeSpan);
            if (!hasNext)
                yield break;
            yield return updates.Current;
        }
    }

    private TimeoutException CreateTimeoutException() =>
        new($"The endpoint sent nothing for {timeout.TotalSeconds:0} seconds. "
            + "The server or model may be overloaded or stuck; try again, or check the endpoint and model.");
}
