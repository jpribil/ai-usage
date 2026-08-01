namespace AIUsageMonitor.UI;

internal sealed class TopicDialog : Form
{
    private readonly TextBox _topicBox;

    private TopicDialog(string? currentTopic, string promptText)
    {
        Text = "AI Usage Monitor";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 128);

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
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            _topicBox.Focus();
            _topicBox.SelectAll();
        };
    }

    internal static string? Prompt(IWin32Window owner, string? currentTopic, string promptText = "Enter your ntfy.sh channel (topic) name:")
    {
        using var dialog = new TopicDialog(currentTopic, promptText);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog._topicBox.Text.Trim()
            : null;
    }
}
