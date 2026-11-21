using Montab.App;
using Montab.Config;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

// Единственный экземпляр: именованный мьютекс держится до выхода процесса,
// хэндл намеренно не закрывается — его освободит система.
PInvoke.CreateMutex(null, true, "montab.single-instance");
if (Marshal.GetLastPInvokeError() == (int)WIN32_ERROR.ERROR_ALREADY_EXISTS)
    return;

var host = new PanelHost(Settings.Load());
host.Start();

while (PInvoke.GetMessage(out MSG msg, default, 0, 0))
{
    PInvoke.TranslateMessage(in msg);
    PInvoke.DispatchMessage(in msg);
}
