namespace AIUsageMonitor.UI;

internal sealed class RouterKeysDialog : Form
{
    private readonly TextBox _openRouterBox;
    private readonly TextBox _nanoGptBox;

    private RouterKeysDialog(string? openRouterKey, string? nanoGptKey, string title, string openRouterLabel, string nanoGptLabel, string saveText, string cancelText)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 190);

        var openRouter = new Label { Text = openRouterLabel, AutoSize = true, Location = new Point(14, 14) };
        _openRouterBox = CreateKeyBox(openRouterKey, new Point(14, 40));
        var nanoGpt = new Label { Text = nanoGptLabel, AutoSize = true, Location = new Point(14, 76) };
        _nanoGptBox = CreateKeyBox(nanoGptKey, new Point(14, 102));
        var save = new Button { Text = saveText, DialogResult = DialogResult.OK, Location = new Point(290, 148), Size = new Size(75, 26) };
        var cancel = new Button { Text = cancelText, DialogResult = DialogResult.Cancel, Location = new Point(371, 148), Size = new Size(75, 26) };

        Controls.AddRange([openRouter, _openRouterBox, nanoGpt, _nanoGptBox, save, cancel]);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => _openRouterBox.Focus();
    }

    private static TextBox CreateKeyBox(string? value, Point location) => new()
    {
        Text = value ?? string.Empty,
        Location = location,
        Size = new Size(432, 23),
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        UseSystemPasswordChar = true
    };

    internal static (string OpenRouter, string NanoGpt)? Prompt(IWin32Window owner, string? openRouterKey, string? nanoGptKey,
        string title, string openRouterLabel, string nanoGptLabel, string saveText, string cancelText)
    {
        using var dialog = new RouterKeysDialog(openRouterKey, nanoGptKey, title, openRouterLabel, nanoGptLabel, saveText, cancelText);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? (dialog._openRouterBox.Text.Trim(), dialog._nanoGptBox.Text.Trim())
            : null;
    }
}
