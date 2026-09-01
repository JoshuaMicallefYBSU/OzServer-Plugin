using System.Drawing;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// A ContextMenuStrip that renders as one of vatSys's own menus rather than a Windows one.
//
// The colours are not reproduced here at all: vatsys.MainForm builds a single vatsys.MenuRenderer
// and publishes it through the public static ComboField.DefaultMenuRenderer, which every built-in
// ComboField/DropDownBox menu is drawn with. Borrowing that exact instance makes this menu identical
// to a built-in one by construction - same palette, same highlight, same border, same margin gutter -
// and it keeps following the loaded profile with no work here. vatsys.MenuRenderer is itself a
// private type, so that property is the only way to reach an instance of it.
//
// VatSysMenuRenderer below is only a fallback for the case where that property is still null (the
// plugin can construct before MainForm has run the line that sets it), and repaints the same roles
// from Colours.GetColour so the menu is never left with the default lilac Office chrome.
static class VatSysContextMenu
{
    static ToolStripRenderer? _fallbackRenderer;

    public static ContextMenuStrip Create() => new()
    {
        Font = MMI.eurofont_winsml,
        ShowImageMargin = false,
    };

    // Resolved per show, not once at construction: DefaultMenuRenderer is set during MainForm's own
    // startup, which may well be after this menu was first built.
    public static void ApplyRenderer(ContextMenuStrip menu) =>
        menu.Renderer = ComboField.DefaultMenuRenderer ?? (_fallbackRenderer ??= new VatSysMenuRenderer());

    // Title row at the top of the menu naming what the menu is acting on (the sector or request the
    // controller right-clicked). Deliberately disabled: it is a label, not a command.
    public static ToolStripMenuItem CreateHeader(string text) => new(text) { Enabled = false };
}

sealed class VatSysMenuRenderer : ToolStripProfessionalRenderer
{
    public VatSysMenuRenderer() : base(new VatSysColourTable()) => RoundedEdges = false;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Colours.GetColour(Colours.Identities.WindowBackground));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Colours.GetColour(Colours.Identities.WindowBorder));
        var bounds = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        // Selected is the hover/keyboard highlight. A disabled item (the header row) must never
        // take it - WinForms still reports Selected for a disabled item the pointer is over.
        var highlight = e.Item.Selected && e.Item.Enabled;
        var colour = highlight
            ? Colours.GetColour(Colours.Identities.WindowButtonSelected)
            : Colours.GetColour(Colours.Identities.WindowBackground);

        using var brush = new SolidBrush(colour);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // NonInteractiveText for a disabled row is what the rest of vatSys uses for "shown, but not
        // something you can act on" - both the header and any action that doesn't apply to the
        // clicked node land here, which is what makes an unavailable action read as unavailable
        // rather than simply missing.
        e.TextColor = !e.Item.Enabled
            ? Colours.GetColour(Colours.Identities.NonInteractiveText)
            : e.Item.Selected
                ? Colours.GetColour(Colours.Identities.HighlightedText)
                : Colours.GetColour(Colours.Identities.InteractiveText);

        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Colours.GetColour(Colours.Identities.ListSeparator));
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }
}

// ToolStripProfessionalRenderer paints the drop-down's own frame and gutter from this table rather
// than from the overrides above, so the profile colours have to be repeated here to stop the
// default lilac Office palette showing through at the edges.
sealed class VatSysColourTable : ProfessionalColorTable
{
    public VatSysColourTable() => UseSystemColors = false;

    public override Color ToolStripDropDownBackground => Colours.GetColour(Colours.Identities.WindowBackground);
    public override Color MenuItemSelected => Colours.GetColour(Colours.Identities.WindowButtonSelected);
    public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
    public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
    public override Color MenuItemBorder => Colours.GetColour(Colours.Identities.WindowBorder);
    public override Color MenuBorder => Colours.GetColour(Colours.Identities.WindowBorder);
    public override Color ImageMarginGradientBegin => Colours.GetColour(Colours.Identities.WindowBackground);
    public override Color ImageMarginGradientMiddle => Colours.GetColour(Colours.Identities.WindowBackground);
    public override Color ImageMarginGradientEnd => Colours.GetColour(Colours.Identities.WindowBackground);
    public override Color SeparatorDark => Colours.GetColour(Colours.Identities.ListSeparator);
    public override Color SeparatorLight => Colours.GetColour(Colours.Identities.ListSeparator);
}
