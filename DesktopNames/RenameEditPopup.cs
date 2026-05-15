namespace DesktopNames;

/// <summary>
/// Borderless TopMost popup with a single TextBox, positioned exactly over a desktop
/// button to give an "inline edit" feel. The overlay form is WS_EX_NOACTIVATE so a child
/// control there can't receive keyboard focus — a peer window can, hence this popup.
///
/// Validation runs on every keystroke (red background = invalid). Enter commits only
/// if valid; Esc cancels; click outside cancels (Deactivate).
/// </summary>
internal sealed class RenameEditPopup : Form
{
    private readonly TextBox _textBox;
    private readonly Func<string, bool> _isValid;
    private readonly Color _invalidBack = Color.FromArgb(255, 200, 200);
    private readonly Color _validBack;

    public string Value => _textBox.Text.Trim();

    public RenameEditPopup(string initial, Rectangle screenBounds, Func<string, bool> isValid, bool isDark)
    {
        _isValid = isValid;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        // Match button rect on screen; expand a touch so the textbox is readable.
        int minH = 22;
        if (screenBounds.Height < minH)
        {
            int pad = (minH - screenBounds.Height + 1) / 2;
            screenBounds.Inflate(0, pad);
        }
        Bounds = screenBounds;

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = initial,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI Variable Text", 10f, FontStyle.Regular),
            BackColor = isDark ? Color.FromArgb(50, 50, 50) : Color.White,
            ForeColor = isDark ? Color.White : Color.Black,
            TextAlign = HorizontalAlignment.Center,
        };
        _validBack = _textBox.BackColor;
        Controls.Add(_textBox);

        _textBox.TextChanged += (_, _) =>
        {
            _textBox.BackColor = _isValid(_textBox.Text) ? _validBack : _invalidBack;
        };

        Shown += (_, _) =>
        {
            _textBox.SelectAll();
            _textBox.Focus();
        };

        Deactivate += (_, _) =>
        {
            // Click-outside cancels. Use BeginInvoke so we don't dispose during the activation flip.
            BeginInvoke(() => { DialogResult = DialogResult.Cancel; Close(); });
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }
        if (keyData == Keys.Enter)
        {
            if (_isValid(_textBox.Text))
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                _textBox.BackColor = _invalidBack;
                System.Media.SystemSounds.Beep.Play();
            }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
