using Montab.App;
using Montab.Config;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

using var singleInstance = new Mutex(initiallyOwned: true, "montab.single-instance", out bool isFirst);
if (!isFirst)
    return;

var settings = Settings.Load();
var panel = new PanelWindow(settings);
panel.Create();

while (PInvoke.GetMessage(out MSG msg, default, 0, 0))
{
    PInvoke.TranslateMessage(in msg);
    PInvoke.DispatchMessage(in msg);
}
