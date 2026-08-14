using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// A GenericButton that can be held permanently "pressed" (sunken, depressed background) rather
// than only while the mouse is actually down - used for the Available/Controlled toggle so the
// active side reads as clicked-in. GenericButton's own pressed-look is driven by a private field
// and a private DrawText method, so it can't be reused directly; this reimplements just enough of
// it (same WindowButtonDepressed/Sunken look) to stay visually consistent with the real thing.
public class ToggleGenericButton : GenericButton
{
    public bool Pressed { get; set; }

    protected override void OnPaint(PaintEventArgs pe)
    {
        if (!Pressed)
        {
            base.OnPaint(pe);
            return;
        }

        using var back = new SolidBrush(Colours.GetColour(Colours.Identities.WindowButtonDepressed));
        using var fore = new SolidBrush(Enabled ? ForeColor : DisabledForeColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        pe.Graphics.FillRectangle(back, ClientRectangle);
        pe.Graphics.DrawString(Text, Font, fore, ClientRectangle, format);
        ControlPaint.DrawBorder3D(pe.Graphics, ClientRectangle, Border3DStyle.Sunken);
    }
}
