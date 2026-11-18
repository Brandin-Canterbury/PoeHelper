using System.Runtime.InteropServices;
using System.Windows;
using PoeHelper.Core.Items;

namespace PoeHelper.App.Native;

/// <summary>
/// Copies the hovered item exactly like a player would: one Ctrl+C (or Ctrl+Alt+C) to the game, read the
/// clipboard, then put back whatever text was there before. No memory reading, no other input.
/// </summary>
public static class ItemCopier
{
    public static async Task<string?> CopyHoveredItemAsync(bool advanced, TimeSpan timeout)
    {
        string? previous = TryGetText();
        TryClear();

        bool ctrlHeld = Win32.IsDown(Win32.VK_CONTROL);
        bool altHeld = Win32.IsDown(Win32.VK_MENU);
        var inputs = new List<Win32.INPUT>();
        if (!ctrlHeld) inputs.Add(Win32.Key(Win32.VK_CONTROL, false));
        if (advanced && !altHeld) inputs.Add(Win32.Key(Win32.VK_MENU, false));
        inputs.Add(Win32.Key('C', false));
        inputs.Add(Win32.Key('C', true));
        if (advanced && !altHeld) inputs.Add(Win32.Key(Win32.VK_MENU, true));
        if (!ctrlHeld) inputs.Add(Win32.Key(Win32.VK_CONTROL, true));
        Win32.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Win32.INPUT>());

        string? text = null;
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(25);
            text = TryGetText();
            if (ItemParser.LooksLikeItem(text)) break;
        }

        if (previous is not null) TrySet(previous);
        return ItemParser.LooksLikeItem(text) ? text : null;
    }

    static string? TryGetText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(10); } // clipboard busy
        }
        return null;
    }

    static void TryClear()
    {
        try { Clipboard.Clear(); } catch (COMException) { }
    }

    static void TrySet(string text)
    {
        try { Clipboard.SetDataObject(text, true); } catch (COMException) { }
    }
}
