using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// A GenericButton that can be held "pressed" rather than only while the mouse is down - used for
// the Available/Controlled toggle, so the active side reads as selected. GenericButton's pressed
// look is driven by a private field and a private DrawText method, so it is redrawn here with the
// same WindowButtonDepressed colour and sunken border.
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
