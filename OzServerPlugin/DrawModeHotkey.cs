using System;
using System.Windows.Forms;

namespace OzServerPlugin;

// D toggles draw mode.
//
// An IMessageFilter rather than a KeyDown handler or Form.KeyPreview: the key has to work wherever
// the controller is looking - the main window, a popped-out ASD - and setting KeyPreview on vatSys's
// own form would change how every one of its own shortcuts is dispatched. A filter sees the message
// before any control does, without altering anything vatSys owns.
//
// D is safe to take. vatSys binds only function keys, the numpad and the arrows (its own text area
// is F9, lat/long points F10), so no plain letter collides with it.
public sealed class DrawModeHotkey : IMessageFilter
{
    const int WM_KEYDOWN = 0x0100;

    readonly MapDrawing _drawing;

    public DrawModeHotkey(MapDrawing drawing)
    {
        _drawing = drawing;
        Application.AddMessageFilter(this);
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg != WM_KEYDOWN || (Keys)(int)message.WParam != Keys.D)
            return false;

        // Modified presses are somebody else's. Ctrl+D and Alt+D must keep whatever meaning they
        // have, and only a bare D is ours.
        if (Control.ModifierKeys != Keys.None)
            return false;

        // Never while text is being entered. A controller typing into the sector search, a text
        // area, or any other field is typing the letter D - swallowing it there would make those
        // fields silently drop a character, which is the kind of fault that gets blamed on
        // anything but a plugin.
        if (IsTextEntry(Control.FromHandle(message.HWnd)))
            return false;

        _drawing.Toggle();

        // Swallowed only once it has actually been handled, so a D that was not ours still reaches
        // whatever would otherwise have had it.
        return true;
    }

    // Covers TextBox, RichTextBox and anything derived from them, plus vatSys's own TextField, which
    // is what its text area editor uses and does not derive from TextBoxBase.
    static bool IsTextEntry(Control? control)
    {
        if (control == null)
            return false;

        if (control is TextBoxBase)
            return true;

        for (var type = control.GetType(); type != null; type = type.BaseType)
        {
            if (type.Name is "TextField" or "TextBoxBase")
                return true;
        }

        return false;
    }
}
