namespace DesktopNames;

/// <summary>Minimal single-line text prompt — Win32 InputBox replacement.</summary>
internal static class InputDialog
{
    public static string? Show(string title, string prompt, string defaultValue, IWin32Window? owner = null)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(380, 130),
            TopMost = true,
        };

        var label  = new Label   { Text = prompt, Left = 12, Top = 12, Width = 356, Height = 20 };
        var text   = new TextBox { Left = 12, Top = 36, Width = 356, Text = defaultValue };
        var ok     = new Button  { Text = "OK",     Left = 212, Top = 80, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button  { Text = "Cancel", Left = 293, Top = 80, Width = 75, DialogResult = DialogResult.Cancel };

        form.Controls.AddRange(new Control[] { label, text, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        text.SelectAll();
        var result = form.ShowDialog(owner);
        return result == DialogResult.OK ? text.Text : null;
    }
}
