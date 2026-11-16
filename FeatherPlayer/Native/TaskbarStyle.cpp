#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#undef GetCurrentTime
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Numerics.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <atomic>
#include <cmath>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace xaml = winrt::Windows::UI::Xaml;
namespace media = winrt::Windows::UI::Xaml::Media;
namespace composition = winrt::Windows::UI::Composition;
using winrt::Windows::UI::Color;

// Private IPC for one Explorer instance. Revision is a seqlock, written last.
struct Configuration
{
    DWORD magic, version, owner, enabled, blur, motion, dark, surface, glow;
    DWORD reserved, revision;
    LONG status, error;
};
static_assert(sizeof(Configuration) == 52);
constexpr DWORD Magic = 0x4c4d5442;
constexpr GUID SiteClass = {0x50e282fa,0xb8a4,0x4b29,{0xb5,0x91,0x81,0x64,0x7a,0xcf,0xe5,0x42}};

static Color Unpack(DWORD value)
{
    return {static_cast<BYTE>(value >> 24),static_cast<BYTE>(value >> 16),
        static_cast<BYTE>(value >> 8),static_cast<BYTE>(value)};
}

struct Target
{
    xaml::Shapes::Rectangle rectangle{nullptr};
    media::Brush original{nullptr};
    composition::Visual originalChild{nullptr};
    winrt::Windows::System::DispatcherQueue queue{nullptr};
    composition::SpriteVisual light{nullptr};
    composition::CompositionRadialGradientBrush gradient{nullptr};
    composition::CompositionPropertySet phase{nullptr};
    bool border = false, styled = false, moving = false;
    float angle = 0;
    std::chrono::steady_clock::time_point motionStarted{};
};

class Watcher : public winrt::implements<Watcher,IObjectWithSite,IVisualTreeServiceCallback2>
{
    winrt::com_ptr<IUnknown> _site;
    winrt::com_ptr<IXamlDiagnostics> _diagnostics;
    winrt::com_ptr<IVisualTreeService3> _service;
    HANDLE _mapping = nullptr, _changed = nullptr;
    Configuration* _shared = nullptr;
    std::mutex _mutex;
    std::unordered_map<InstanceHandle,std::shared_ptr<Target>> _targets;
    std::atomic<bool> _stopped{false};

    Configuration Read()
    {
        Configuration copy{};
        for (int attempt = 0; attempt < 16 && _shared; ++attempt)
        {
            DWORD before = _shared->revision;
            MemoryBarrier();
            if (before & 1) { SwitchToThread(); continue; }
            memcpy(&copy,_shared,sizeof(copy));
            MemoryBarrier();
            if (before == _shared->revision && copy.magic == Magic && copy.version == 1) return copy;
        }
        copy.enabled = 0;
        return copy;
    }

    void Report(LONG status, HRESULT error = S_OK)
    {
        if (_shared)
        {
            InterlockedExchange(&_shared->error,error);
            InterlockedExchange(&_shared->status,status);
        }
    }

    static bool InTaskbar(xaml::FrameworkElement element)
    {
        for (int depth = 0; element && depth < 32; ++depth)
        {
            if (winrt::get_class_name(element) == L"Taskbar.TaskbarFrame") return true;
            element = media::VisualTreeHelper::GetParent(element).try_as<xaml::FrameworkElement>();
        }
        return false;
    }

    static void Restore(Target& target)
    {
        if (!target.styled) return;
        if (target.phase) target.phase.StopAnimation(L"Angle");
        target.rectangle.Fill(target.original);
        xaml::Hosting::ElementCompositionPreview::SetElementChildVisual(target.rectangle,target.originalChild);
        target.light = nullptr; target.gradient = nullptr; target.phase = nullptr;
        target.moving = false; target.styled = false;
    }

    static void Apply(Target& target, Configuration const& config)
    {
        if (!config.enabled) { Restore(target); return; }
        target.styled = true;
        if (target.border)
        {
            target.rectangle.Fill(media::SolidColorBrush(Color{0,0,0,0}));
            return;
        }

        auto surface = Unpack(config.surface);
        if (config.blur)
        {
            auto acrylic = target.rectangle.Fill().try_as<media::AcrylicBrush>();
            if (!acrylic || acrylic == target.original) acrylic = media::AcrylicBrush();
            acrylic.BackgroundSource(media::AcrylicBackgroundSource::Backdrop);
            acrylic.TintColor(Color{255,surface.R,surface.G,surface.B});
            acrylic.TintOpacity(surface.A / 255.0);
            acrylic.FallbackColor(surface);
            target.rectangle.Fill(acrylic);
        }
        else target.rectangle.Fill(media::SolidColorBrush(surface));

        if (!target.light)
        {
            auto compositor = xaml::Hosting::ElementCompositionPreview::GetElementVisual(target.rectangle).Compositor();
            target.gradient = compositor.CreateRadialGradientBrush();
            target.gradient.MappingMode(composition::CompositionMappingMode::Relative);
            target.gradient.EllipseRadius({.7f,1.8f});
            for (float offset : {0.f,.45f,.75f,1.f})
                target.gradient.ColorStops().Append(compositor.CreateColorGradientStop(offset,Color{0,0,0,0}));
            target.phase = compositor.CreatePropertySet();
            target.phase.InsertScalar(L"Angle",0);
            auto position = compositor.CreateExpressionAnimation(
                L"Vector2(0.5 + 0.36 * Cos(p.Angle), 0.47 + 0.32 * Sin(p.Angle))");
            position.SetReferenceParameter(L"p",target.phase);
            target.gradient.StartAnimation(L"EllipseCenter",position);
            target.light = compositor.CreateSpriteVisual();
            target.light.RelativeSizeAdjustment({1,1});
            target.light.Brush(target.gradient);
            xaml::Hosting::ElementCompositionPreview::SetElementChildVisual(target.rectangle,target.light);
        }

        auto glow = Unpack(config.glow);
        constexpr float weights[] = {1,.52f,.16f,0};
        auto compositor = target.light.Compositor();
        for (unsigned i = 0; i < 4; ++i)
        {
            Color color{static_cast<BYTE>(glow.A * weights[i]),glow.R,glow.G,glow.B};
            auto transition = compositor.CreateColorKeyFrameAnimation();
            transition.Duration(std::chrono::milliseconds(900));
            transition.InsertKeyFrame(1,color,compositor.CreateCubicBezierEasingFunction({.42f,0},{.58f,1}));
            target.gradient.ColorStops().GetAt(i).StartAnimation(L"Color",transition);
        }
        bool moving = config.motion && glow.A > 0;
        if (moving != target.moving)
        {
            if (moving)
            {
                auto rotation = compositor.CreateScalarKeyFrameAnimation();
                rotation.InsertKeyFrame(0,target.angle);
                rotation.InsertKeyFrame(1,target.angle + 6.283185307f,compositor.CreateLinearEasingFunction());
                rotation.Duration(std::chrono::seconds(24));
                rotation.IterationBehavior(composition::AnimationIterationBehavior::Forever);
                target.phase.StartAnimation(L"Angle",rotation);
                target.motionStarted = std::chrono::steady_clock::now();
            }
            else
            {
                float elapsed = std::chrono::duration<float>(std::chrono::steady_clock::now() - target.motionStarted).count();
                target.angle = std::fmod(target.angle + elapsed * 6.283185307f / 24,6.283185307f);
                target.phase.StopAnimation(L"Angle");
                target.phase.InsertScalar(L"Angle",target.angle);
            }
            target.moving = moving;
        }
    }

    void Publish(Configuration config)
    {
        std::vector<std::shared_ptr<Target>> targets;
        { std::lock_guard lock(_mutex); for (auto const& pair : _targets) targets.push_back(pair.second); }
        auto self = get_strong();
        for (auto const& target : targets)
            target->queue.TryEnqueue([self,target,config]
            {
                try
                {
                    auto latest = config;
                    if (self->_stopped) latest.enabled = 0;
                    Apply(*target,latest);
                    if (!target->border) self->Report(latest.enabled ? 2 : 1);
                }
                catch (...) { self->Report(-1,winrt::to_hresult()); }
            });
    }

    void Monitor()
    {
        bool apartment = false;
        try
        {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            apartment = true;
            winrt::check_hresult(_service->AdviseVisualTreeChange(this));
            Report(1);
            auto config = Read();
            HANDLE owner = OpenProcess(SYNCHRONIZE,FALSE,config.owner);
            if (owner)
            {
                Publish(config);
                HANDLE handles[] = {owner,_changed};
                while (!_stopped)
                {
                    DWORD result = WaitForMultipleObjects(2,handles,FALSE,INFINITE);
                    if (result != WAIT_OBJECT_0 + 1) break;
                    auto latest = Read();
                    if (latest.owner != config.owner) break;
                    Publish(latest);
                }
                CloseHandle(owner);
            }
            _stopped = true;
            config.enabled = 0;
            Publish(config);
            _service->UnadviseVisualTreeChange(this);
        }
        catch (...)
        {
            auto error = winrt::to_hresult();
            _stopped = true;
            try { Publish(Configuration{}); _service->UnadviseVisualTreeChange(this); } catch (...) { }
            Report(-1,error);
        }
        if (apartment) winrt::uninit_apartment();
    }

public:
    ~Watcher()
    {
        if (_shared) UnmapViewOfFile(_shared);
        if (_mapping) CloseHandle(_mapping);
        if (_changed) CloseHandle(_changed);
    }
    HRESULT __stdcall SetSite(IUnknown* site) noexcept override
    {
        try
        {
            if (!site) { _stopped = true; if (_changed) SetEvent(_changed); return S_OK; }
            _site.copy_from(site);
            _diagnostics = _site.as<IXamlDiagnostics>();
            _service = _site.as<IVisualTreeService3>();
            auto suffix = std::to_wstring(GetCurrentProcessId());
            _mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,(L"Local\\LightMusicTaskbar-" + suffix).c_str());
            if (!_mapping) return HRESULT_FROM_WIN32(GetLastError());
            _shared = static_cast<Configuration*>(MapViewOfFile(_mapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Configuration)));
            if (!_shared) return HRESULT_FROM_WIN32(GetLastError());
            _changed = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE,FALSE,(L"Local\\LightMusicTaskbarChanged-" + suffix).c_str());
            if (!_changed) return HRESULT_FROM_WIN32(GetLastError());
            std::thread([self = get_strong()] { self->Monitor(); }).detach();
            return S_OK;
        }
        catch (...) { return winrt::to_hresult(); }
    }
    HRESULT __stdcall GetSite(REFIID iid, void** result) noexcept override
    {
        return _site ? _site->QueryInterface(iid,result) : E_FAIL;
    }
    HRESULT __stdcall OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override
    {
        try
        {
            if (mutation == Remove)
            {
                std::shared_ptr<Target> target;
                {
                    std::lock_guard lock(_mutex);
                    auto found = _targets.find(element.Handle);
                    if (found == _targets.end()) return S_OK;
                    target = found->second; _targets.erase(found);
                }
                if (target->queue.HasThreadAccess()) Restore(*target);
                else target->queue.TryEnqueue([target] { try { Restore(*target); } catch (...) { } });
                return S_OK;
            }
            if (mutation != Add) return S_OK;
            std::wstring name = element.Name ? element.Name : L"";
            std::wstring type = element.Type ? element.Type : L"";
            SysFreeString(element.Name); SysFreeString(element.Type);
            SysFreeString(element.SrcInfo.FileName); SysFreeString(element.SrcInfo.Hash);
            if (_stopped || type != L"Windows.UI.Xaml.Shapes.Rectangle" ||
                (name != L"BackgroundFill" && name != L"BackgroundStroke")) return S_OK;
            winrt::Windows::Foundation::IInspectable object{nullptr};
            winrt::check_hresult(_diagnostics->GetIInspectableFromHandle(element.Handle,
                reinterpret_cast<::IInspectable**>(winrt::put_abi(object))));
            auto rectangle = object.try_as<xaml::Shapes::Rectangle>();
            if (!rectangle || !InTaskbar(rectangle)) return S_OK;
            auto target = std::make_shared<Target>();
            target->rectangle = rectangle; target->original = rectangle.Fill();
            target->originalChild = xaml::Hosting::ElementCompositionPreview::GetElementChildVisual(rectangle);
            target->queue = winrt::Windows::System::DispatcherQueue::GetForCurrentThread();
            target->border = name == L"BackgroundStroke";
            if (!target->queue) return S_OK;
            {
                std::lock_guard lock(_mutex);
                if (!_targets.try_emplace(element.Handle,target).second) return S_OK;
            }
            auto config = Read();
            Apply(*target,config);
            if (!target->border) Report(config.enabled ? 2 : 1);
            return S_OK;
        }
        catch (...) { Report(-1,winrt::to_hresult()); return S_OK; }
    }
    HRESULT __stdcall OnElementStateChanged(InstanceHandle,VisualElementState,LPCWSTR) noexcept override { return S_OK; }
};

class Factory : public winrt::implements<Factory,IClassFactory>
{
public:
    HRESULT __stdcall CreateInstance(IUnknown* outer, REFIID iid, void** result) noexcept override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        try { return winrt::make_self<Watcher>()->QueryInterface(iid,result); }
        catch (...) { return winrt::to_hresult(); }
    }
    HRESULT __stdcall LockServer(BOOL) noexcept override { return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** result)
{
    if (clsid != SiteClass) return CLASS_E_CLASSNOTAVAILABLE;
    try { return winrt::make_self<Factory>()->QueryInterface(iid,result); }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" HRESULT __stdcall InitializeLightTaskbar(DWORD explorer) try
{
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&InitializeLightTaskbar),&module)) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t path[32768]{};
    if (!GetModuleFileNameW(module,path,32768)) return HRESULT_FROM_WIN32(GetLastError());
    auto xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!xaml) return HRESULT_FROM_WIN32(GetLastError());
    auto initialize = reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(GetProcAddress(xaml,"InitializeXamlDiagnosticsEx"));
    HRESULT result = E_NOINTERFACE;
    if (initialize)
        for (int connection = 1; connection <= 64; ++connection)
        {
            std::thread attempt([&]
            {
                bool apartment = false;
                try
                {
                    winrt::init_apartment(winrt::apartment_type::multi_threaded);
                    apartment = true;
                    auto name = L"VisualDiagConnection" + std::to_wstring(connection);
                    result = initialize(name.c_str(),explorer,nullptr,path,SiteClass,nullptr);
                }
                catch (...) { result = winrt::to_hresult(); }
                if (apartment) winrt::uninit_apartment();
            });
            attempt.join();
            if (SUCCEEDED(result)) break;
        }
    FreeLibrary(xaml);
    return result;
}
catch (...) { return winrt::to_hresult(); }
