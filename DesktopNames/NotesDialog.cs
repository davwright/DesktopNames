namespace DesktopNames;

/// <summary>
/// Multiline note editor anchored just above a desktop's overlay button. Enter inserts a
/// newline; Ctrl+Enter saves; Esc cancels. Returns null on cancel, otherwise the new
/// (possibly empty) note text.
/// </summary>
internal static class NotesDialog
{
    private const int W = 420;
    private const int H = 240;

    public static string? Show(string title, string prompt, string defaultValue,
                               Rectangle anchorScreenRect, IWin32Window? owner = null)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.Manual,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(W, H),
            TopMost = true,
        };

        var label = new Label
        {
            Text = prompt,
            Left = 12, Top = 10, Width = W - 24, Height = 20
        };
        var text = new TextBox
        {
            Left = 12, Top = 34, Width = W - 24, Height = H - 80,
            Text = defaultValue,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = false,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true
        };
        var hint = new Label
        {
            Text = "Ctrl+Enter to save · Esc to cancel",
            Left = 12, Top = H - 28, Width = 220, Height = 18,
            ForeColor = SystemColors.GrayText
        };
        var ok = new Button { Text = "OK",     Left = W - 174, Top = H - 32, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = W -  90, Top = H - 32, Width = 75, DialogResult = DialogResult.Cancel };

        // Enter must insert a newline (multiline), so don't set AcceptButton. Esc → Cancel.
        form.AcceptButton = null;
        form.CancelButton = cancel;
        form.Controls.AddRange(new Control[] { label, text, hint, ok, cancel });

        // Ctrl+Enter triggers OK as a power-user save shortcut.
        text.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                form.DialogResult = DialogResult.OK;
                form.Close();
                e.SuppressKeyPress = true;
            }
        };

        // Anchor just above the button. Clamp to the working area of the screen the
        // anchor lives on. If there's no headroom (taskbar at top), drop below instead.
        var screen = Screen.FromRectangle(anchorScreenRect);
        int x = anchorScreenRect.Left;
        int y = anchorScreenRect.Top - H - 8;
        if (y < screen.WorkingArea.Top) y = anchorScreenRect.Bottom + 8;
        if (x + W > screen.WorkingArea.Right) x = screen.WorkingArea.Right - W;
        if (x < screen.WorkingArea.Left) x = screen.WorkingArea.Left;
        form.Location = new Point(x, y);

        text.Select(text.TextLength, 0); // cursor at end, no selection

        return form.ShowDialog(owner) == DialogResult.OK ? text.Text : null;
    }
}
