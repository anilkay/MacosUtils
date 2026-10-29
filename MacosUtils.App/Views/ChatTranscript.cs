using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace MacosUtils.App.Views;

/// <summary>
/// The AI Chat transcript: one bubble per message under a "You" / "Assistant" header, plus notes
/// and errors. Model text is shown as plain, selectable text, never interpreted as markup. Keeps
/// the raw messages so the chat can be exported as Markdown. Follows new text only while it is
/// scrolled to the bottom, so reading earlier messages is not interrupted.
/// </summary>
public sealed class ChatTranscript : UserControl
{
    private readonly StackPanel _panel = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly ScrollViewer _scroll;
    private readonly List<(string Role, StringBuilder Text)> _messages = [];
    private SelectableTextBlock? _current;
    private StringBuilder? _currentText;

    public ChatTranscript()
    {
        _scroll = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _scroll;
    }

    /// <summary>True when there is at least one message (notes do not count).</summary>
    public bool HasMessages => _messages.Count > 0;

    public void AddUserMessage(string text)
    {
        _messages.Add(("You", new StringBuilder(text)));
        AddBubble("You", text, "user");
    }

    public void BeginAssistantMessage()
    {
        _currentText = new StringBuilder();
        _messages.Add(("Assistant", _currentText));
        _current = AddBubble("Assistant", "…", "assistant");
    }

    public void AppendAssistantText(string text)
    {
        if (_current is null || _currentText is null)
            BeginAssistantMessage();
        _currentText!.Append(text);
        var follow = IsAtBottom();
        _current!.Text = _currentText.ToString();
        if (follow)
            ScrollToEnd();
    }

    public void EndAssistantMessage()
    {
        if (_current is not null && _currentText is { Length: 0 })
        {
            // Nothing arrived (cancelled or failed before the first word): drop the empty bubble.
            _panel.Children.RemoveAt(_panel.Children.Count - 1);
            _messages.RemoveAt(_messages.Count - 1);
        }
        _current = null;
        _currentText = null;
    }

    public void AddNote(string text) => AddBubble(null, text, "note", muted: true);

    public void AddError(string text) => AddBubble(null, text, "note", error: true);

    public void ClearTranscript()
    {
        _panel.Children.Clear();
        _messages.Clear();
        _current = null;
        _currentText = null;
    }

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        foreach (var (role, text) in _messages)
            builder.Append("## ").Append(role).Append("\n\n").Append(text).Append("\n\n");
        return builder.ToString();
    }

    private SelectableTextBlock AddBubble(string? header, string text, string kind, bool muted = false, bool error = false)
    {
        var body = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (muted)
            body.Classes.Add("muted");
        if (error)
            body.Classes.Add("error");
        var content = new StackPanel();
        if (header is not null)
            content.Children.Add(new TextBlock { Text = header, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        content.Children.Add(body);
        var bubble = new Border { Child = content };
        bubble.Classes.Add("bubble");
        bubble.Classes.Add(kind);
        var follow = IsAtBottom();
        _panel.Children.Add(bubble);
        if (follow || header == "You")
            ScrollToEnd();
        return body;
    }

    private bool IsAtBottom() => _scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - 24;

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
}
