#include <windows.h>
#include <dwmapi.h>

struct AccentPolicy { int state; int flags; DWORD color; int animation; };
struct CompositionData { int attribute; void* data; SIZE_T size; };
using SetComposition = BOOL(WINAPI*)(HWND, CompositionData*);

extern "C" __declspec(dllexport) void ApplyMenuSurface(HWND window, int dark, int blur, DWORD tint, int layered)
{
    if (!IsWindow(window)) return;
    int rounded = 2;
    DWORD border = 0xfffffffe;
    DwmSetWindowAttribute(window, 20, &dark, sizeof(dark));
    DwmSetWindowAttribute(window, 33, &rounded, sizeof(rounded));
    DwmSetWindowAttribute(window, 34, &border, sizeof(border));
    if (!layered)
    {
        MARGINS margins{ -1, -1, -1, -1 };
        DwmExtendFrameIntoClientArea(window, &margins);
    }
    auto setComposition = reinterpret_cast<SetComposition>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowCompositionAttribute"));
    if (setComposition)
    {
        AccentPolicy policy{ blur == 0 ? 0 : 3, 0, tint, 0 };
        CompositionData data{ 19, &policy, sizeof(policy) };
        setComposition(window, &data);
    }
    int backdrop = blur == 2 && !layered ? 3 : 1;
    DwmSetWindowAttribute(window, 38, &backdrop, sizeof(backdrop));
}
