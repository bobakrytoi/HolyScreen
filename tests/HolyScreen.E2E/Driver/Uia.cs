using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;

namespace HolyScreen.E2E.Driver;

/// <summary>Finds the app's controls through UI Automation (screen rectangles in physical pixels for this PMv2 process).</summary>
internal static class Uia
{
    /// <summary>
    /// Bounding rectangle of the element whose automation id — or, failing that, name — is <paramref name="key"/>, in
    /// any visible window of the app (overlay, history, beautify, settings…), optionally only windows whose title
    /// starts with <paramref name="titlePrefix"/>.
    /// </summary>
    public static Rect? Find(AppProcess app, string key, string titlePrefix = "") =>
        Locate(app, key, titlePrefix)?.Rect;

    /// <summary>The UI Automation name of a visible element — for a text block, its text.</summary>
    public static string? NameOf(AppProcess app, string key, string titlePrefix = "") =>
        Locate(app, key, titlePrefix)?.Name;

    private static (Rect Rect, string Name)? Locate(AppProcess app, string key, string titlePrefix) =>
        Locate(app, AutomationElement.AutomationIdProperty, key, titlePrefix) ?? Locate(app, AutomationElement.NameProperty, key, titlePrefix);

    private static (Rect Rect, string Name)? Locate(AppProcess app, AutomationProperty property, string key, string titlePrefix)
    {
        foreach (WindowRecord window in WindowQuery.Showing(app.Id, titlePrefix))
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(window.Handle);
                AutomationElement? element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(property, key));
                if (element is null) continue;
                Rect rect = element.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0 && !element.Current.IsOffscreen) return (rect, element.Current.Name);
            }
            catch (ElementNotAvailableException)
            {
                // window went away while we looked
            }
        }

        return null;
    }

    public static Rect WaitFor(AppProcess app, string key, TimeSpan timeout, string titlePrefix = "")
    {
        Rect? found = null;
        Wait.Until(() => (found = Find(app, key, titlePrefix)) is not null, timeout, 60);
        if (found is null) throw new E2EFailure($"control '{key}' did not appear");
        return found.Value;
    }

    public static void Click(Rect rect)
    {
        Simulate.MoveTo((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
        System.Threading.Thread.Sleep(120); // let hover feedback run, like a real pointer
        Simulate.LeftDown();
        System.Threading.Thread.Sleep(60);
        Simulate.LeftUp();
    }
}
