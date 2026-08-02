using AIUsageMonitor.Services;

namespace AIUsageMonitor.UI;

internal sealed class TopicDialog : Form
{
    private readonly TextBox _topicBox;
    private readonly Func<string, Task<NtfySendResult>>? _testSender;
    private readonly string _testSuccessText;
    private readonly string _testFailurePrefix;
    private readonly Label? _testResult;
    private readonly Button? _testButton;

    private TopicDialog(string? currentTopic, string promptText, Func<string, Task<NtfySendResult>>? testSender, string testButtonText, string testSuccessText, string testFailurePrefix)
    {
        _testSender = testSender;
        _testSuccessText = testSuccessText;
        _testFailurePrefix = testFailurePrefix;
        Text = "AI Usage Monitor";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, testSender is null ? 128 : 160);

        var prompt = new Label
        {
            Text = promptText,
            AutoSize = true,
            Location = new Point(14, 14)
        };
        _topicBox = new TextBox
        {
            Text = currentTopic ?? string.Empty,
            Location = new Point(14, 40),
            Size = new Size(392, 23),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(250, 86),
            Size = new Size(75, 26)
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(331, 86),
            Size = new Size(75, 26)
        };

        Controls.AddRange([prompt, _topicBox, ok, cancel]);
        if (testSender is not null)
        {
            _testButton = new Button
            {
                Text = testButtonText,
                Location = new Point(14, 84),
                Size = new Size(120, 26)
            };
            _testButton.Click += SendTestAsync;
            _testResult = new Label
            {
                AutoEllipsis = true,
                Location = new Point(14, 116),
                Size = new Size(392, 25)
            };
            Controls.AddRange([_testButton, _testResult]);
        }

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            _topicBox.Focus();
            _topicBox.SelectAll();
        };
    }

    private async void SendTestAsync(object? sender, EventArgs e)
    {
        if (_testSender is null || _testButton is null || _testResult is null)
        {
            return;
        }

        var topic = _topicBox.Text.Trim();
        _testButton.Enabled = false;
        try
        {
            var result = await _testSender(topic);
            _testResult.ForeColor = result.Succeeded ? Color.ForestGreen : Color.Firebrick;
            _testResult.Text = result.Succeeded ? _testSuccessText : $"{_testFailurePrefix} {result.Error}";
        }
        catch (Exception exception)
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = $"{_testFailurePrefix} {exception.Message}";
        }
        finally
        {
            _testButton.Enabled = true;
        }
    }

    internal static string? Prompt(IWin32Window owner, string? currentTopic, string promptText = "Enter your ntfy.sh channel (topic) name:",
        Func<string, Task<NtfySendResult>>? testSender = null, string testButtonText = "Send test", string testSuccessText = "Test sent.", string testFailurePrefix = "Test failed:")
    {
        using var dialog = new TopicDialog(currentTopic, promptText, testSender, testButtonText, testSuccessText, testFailurePrefix);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog._topicBox.Text.Trim()
            : null;
    }
}
