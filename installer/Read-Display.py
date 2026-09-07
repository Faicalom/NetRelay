"""Read window geometry/DPI only; does not change display settings or window state."""
import ctypes as c, ctypes.wintypes as w, json
u=c.WinDLL('user32',use_last_error=True)
u.SetThreadDpiAwarenessContext.argtypes=[c.c_void_p]
u.SetThreadDpiAwarenessContext.restype=c.c_void_p
u.SetThreadDpiAwarenessContext(c.c_void_p(-4))
u.GetWindowDpiAwarenessContext.argtypes=[w.HWND]
u.GetWindowDpiAwarenessContext.restype=c.c_void_p
u.GetAwarenessFromDpiAwarenessContext.argtypes=[c.c_void_p]
u.GetDpiForWindow.argtypes=[w.HWND]
u.GetDpiForWindow.restype=w.UINT
results=[]
@c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM)
def enum(hwnd,_):
    name=c.create_unicode_buffer(512)
    u.GetWindowTextW(hwnd,name,512)
    if name.value=='Net Relay for Windows':
        r=w.RECT(); client=w.RECT()
        u.GetWindowRect(hwnd,c.byref(r));u.GetClientRect(hwnd,c.byref(client))
        dpi=u.GetDpiForWindow(hwnd)
        results.append(dict(hwnd=hwnd,dpi=dpi,scale=dpi/96,
            window=[r.left,r.top,r.right-r.left,r.bottom-r.top],
            client=[client.right,client.bottom],maximized=bool(u.IsZoomed(hwnd)),
            awareness=u.GetAwarenessFromDpiAwarenessContext(u.GetWindowDpiAwarenessContext(hwnd))))
    return True
u.EnumWindows(enum,0)
print(json.dumps(dict(screen=[u.GetSystemMetrics(0),u.GetSystemMetrics(1)],windows=results)))
