using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using vatsys;

namespace OzServerPlugin;

// vatSys's TreeViewEx for an owner-drawn tree (DrawMode = OwnerDrawText), with DrawNode raised
// only while the control is painting.
//
// The native tree view does not only run its custom-draw cycle to paint. It runs the same cycle
// to measure: whenever its layout is recalculated - redraw switched back on after a rebuild, a
// node's text changed, a node expanded or collapsed - it sends the item pre-paint and post-paint
// notifications for every node in the tree, hidden ones included, so that a handler can change
// the font before the text is measured. WinForms turns each post-paint into a DrawNode event,
// handing it a Graphics on the control's own window DC.
//
// A DrawNode handler that draws there is drawing straight on the screen, outside any paint and
// outside the control's double buffer, at whatever TreeNode.Bounds says while the layout is half
// way through changing. That left arrows and labels on rows they did not belong to until the row
// was next repainted - and, with a call per node, held the UI thread for a quarter of a second or
// more on every expand, collapse and rebuild of a few hundred sectors.
//
// The pre-paint notification is left alone: it is what selects a node's font for the measurement.
sealed class SectorTreeView : TreeViewEx
{
    const int WM_PAINT = 0x000F;
    const int WM_PRINT = 0x0317;
    const int WM_PRINTCLIENT = 0x0318;
    // WM_NOTIFY as WinForms reflects it back to the control that raised it (WM_REFLECT + WM_NOTIFY).
    const int WM_REFLECT_NOTIFY = 0x204E;
    const int NM_CUSTOMDRAW = -12;
    const int CDDS_ITEMPOSTPAINT = 0x00010002;
    const int CDRF_DODEFAULT = 0;

    // NMHDR is { HWND hwndFrom; UINT_PTR idFrom; UINT code; }, padded to pointer alignment, and
    // NMCUSTOMDRAW's dwDrawStage follows it.
    static readonly int NotifyCodeOffset = IntPtr.Size * 2;
    static readonly int DrawStageOffset = IntPtr.Size * 3;

    // A count rather than a flag: WM_PRINT is answered by sending WM_PRINTCLIENT.
    int _paintDepth;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_PAINT:
            case WM_PRINT:
            case WM_PRINTCLIENT:
                _paintDepth++;
                try
                {
                    base.WndProc(ref m);
                }
                finally
                {
                    _paintDepth--;
                }

                return;

            case WM_REFLECT_NOTIFY when _paintDepth == 0 && IsItemPostPaint(m.LParam):
                // Measuring, not painting: there is nothing to draw, so DrawNode is not raised.
                m.Result = (IntPtr)CDRF_DODEFAULT;
                return;
        }

        base.WndProc(ref m);
    }

    static bool IsItemPostPaint(IntPtr notification) =>
        notification != IntPtr.Zero
        && Marshal.ReadInt32(notification, NotifyCodeOffset) == NM_CUSTOMDRAW
        && Marshal.ReadInt32(notification, DrawStageOffset) == CDDS_ITEMPOSTPAINT;
}
