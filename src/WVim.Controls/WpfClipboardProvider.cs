using System.Windows;
using WVim.Core.Registers;

namespace WVim.Controls;

public class WpfClipboardProvider : IClipboardProvider
{
    public string GetText()
    {
        try { return Clipboard.GetText(); }
        catch { return ""; }
    }

    public void SetText(string text)
    {
        try { Clipboard.SetText(text); }
        catch { }
    }
}
