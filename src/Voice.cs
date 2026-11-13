using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDictation
{
    internal sealed class ComposerTextBox : TextBox
    {
        internal bool Composing { get; private set; }
        internal event Action CompositionFinished;
        internal bool TraceKeys;
        protected override void WndProc(ref Message message)
        {
            if (TraceKeys)
            {
                int m = message.Msg;
                if ((m == 0x100 || m == 0x101 || m == 0x104 || m == 0x105) && (message.WParam.ToInt64() == 0x12 || message.WParam.ToInt64() == 0x11))
                {
                    long l = message.LParam.ToInt64();
                    HoldTrace.Add("editor " + (m == 0x100 ? "KEYDOWN" : m == 0x101 ? "KEYUP" : m == 0x104 ? "SYSKEYDOWN" : "SYSKEYUP") + " vk=" + message.WParam.ToInt64().ToString("X") +
                        " prevDown=" + ((l >> 30) & 1) + " repeat=" + (l & 0xFFFF));
                }
                else if (m == 0x010D) HoldTrace.Add("editor IME_STARTCOMPOSITION");
                else if (m == 0x010E) HoldTrace.Add("editor IME_ENDCOMPOSITION");
                else if (m == 0x0007) HoldTrace.Add("editor SETFOCUS");
            }
            if (message.Msg == 0x010D) Composing = true; // WM_IME_STARTCOMPOSITION
            base.WndProc(ref message);
            if (message.Msg == 0x010E)
            {
                Composing = false;
                if (CompositionFinished != null) CompositionFinished();
            }
        }
    }
}
