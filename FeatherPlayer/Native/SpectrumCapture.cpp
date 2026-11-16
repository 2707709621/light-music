#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <winrt/base.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <complex>
#include <memory>
#include <mutex>
#include <thread>

// Process loopback initialization follows Microsoft's ApplicationLoopback sample.
// Spectrum grouping and dB normalization follow the wpfsvl/FreqFreak approach.
namespace
{
constexpr int Size = 2048, Bands = 32, Rate = 44100;
constexpr float Pi = 3.14159265358979323846f;

struct CaptureState
{
    HANDLE stop = CreateEventW(nullptr,TRUE,FALSE,nullptr);
    HANDLE activated = CreateEventW(nullptr,TRUE,FALSE,nullptr);
    HANDLE ready = CreateEventW(nullptr,FALSE,FALSE,nullptr);
    std::atomic<bool> cancelled{false};
    std::atomic<HRESULT> status{S_FALSE};
    std::atomic<HRESULT> activationResult{E_PENDING};
    std::mutex mutex;
    winrt::com_ptr<IAudioClient> client;
    std::array<float,Bands> bands{};
    ~CaptureState()
    {
        if (stop) CloseHandle(stop);
        if (activated) CloseHandle(activated);
        if (ready) CloseHandle(ready);
    }
};

class Activation : public winrt::implements<Activation,IActivateAudioInterfaceCompletionHandler>
{
    std::shared_ptr<CaptureState> _state;
public:
    explicit Activation(std::shared_ptr<CaptureState> state) : _state(std::move(state)) { }
    HRESULT __stdcall ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) noexcept override
    {
        try
        {
            HRESULT result = E_FAIL;
            winrt::com_ptr<IUnknown> object;
            winrt::check_hresult(operation->GetActivateResult(&result,object.put()));
            winrt::check_hresult(result);
            auto client = object.as<IAudioClient>();
            if (!_state->cancelled)
            {
                std::lock_guard lock(_state->mutex);
                _state->client = std::move(client);
            }
            _state->activationResult = S_OK;
        }
        catch (...) { _state->activationResult = winrt::to_hresult(); }
        SetEvent(_state->activated);
        return S_OK;
    }
};

class Processor
{
    std::array<std::array<float,Size>,2> _ring{};
    std::array<float,Size> _window{};
    std::array<float,Size / 2> _power{};
    std::array<std::complex<float>,Size> _fft{};
    int _position = 0, _hop = 0;

    void Transform(int channel)
    {
        for (int i = 0; i < Size; ++i) _fft[i] = {_ring[channel][(_position + i) % Size] * _window[i],0};
        for (int i = 1, j = 0; i < Size; ++i)
        {
            int bit = Size >> 1;
            for (; j & bit; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) std::swap(_fft[i],_fft[j]);
        }
        for (int length = 2; length <= Size; length <<= 1)
        {
            auto step = std::polar(1.f,-2 * Pi / length);
            for (int offset = 0; offset < Size; offset += length)
            {
                std::complex<float> factor{1,0};
                for (int j = 0; j < length / 2; ++j)
                {
                    auto even = _fft[offset + j], odd = factor * _fft[offset + j + length / 2];
                    _fft[offset + j] = even + odd;
                    _fft[offset + j + length / 2] = even - odd;
                    factor *= step;
                }
            }
        }
    }

    void Publish(CaptureState& state)
    {
        // Combine channel power so opposite-phase stereo does not cancel out.
        _power.fill(0);
        for (int channel = 0; channel < 2; ++channel)
        {
            Transform(channel);
            for (int bin = 0; bin < Size / 2; ++bin) _power[bin] += std::norm(_fft[bin]) * .5f;
        }
        std::array<float,Bands> levels{};
        for (int band = 0; band < Bands; ++band)
        {
            int first = std::clamp(static_cast<int>(50 * std::pow(320.0,band / 32.0) * Size / Rate),1,Size / 2 - 1);
            int last = std::clamp(static_cast<int>(50 * std::pow(320.0,(band + 1) / 32.0) * Size / Rate),first + 1,Size / 2);
            float power = 0;
            for (int bin = first; bin < last; ++bin) power += _power[bin];
            float magnitude = std::sqrt(power / (last - first)) * 4 / Size;
            float db = 20 * std::log10(std::max(magnitude,0.000001f));
            levels[band] = std::clamp((db + 72) / 62,0.f,1.f);
        }
        std::lock_guard lock(state.mutex); state.bands = levels;
    }

public:
    Processor()
    {
        for (int i = 0; i < Size; ++i) _window[i] = .5f - .5f * std::cos(2 * Pi * i / (Size - 1));
    }
    void Add(int16_t const* samples, UINT32 frames, CaptureState& state)
    {
        for (UINT32 frame = 0; frame < frames; ++frame)
        {
            _ring[0][_position] = samples ? samples[frame * 2] / 32768.f : 0;
            _ring[1][_position] = samples ? samples[frame * 2 + 1] / 32768.f : 0;
            _position = (_position + 1) % Size;
            if (++_hop == Size / 2) { _hop = 0; Publish(state); }
        }
    }
};

void Capture(std::shared_ptr<CaptureState> state, DWORD process) noexcept
{
    bool apartment = false;
    winrt::com_ptr<IAudioClient> client;
    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded); apartment = true;
        AUDIOCLIENT_ACTIVATION_PARAMS parameters{};
        parameters.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        parameters.ProcessLoopbackParams.TargetProcessId = process;
        parameters.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
        PROPVARIANT value{};
        value.vt = VT_BLOB; value.blob.cbSize = sizeof(parameters);
        value.blob.pBlobData = reinterpret_cast<BYTE*>(&parameters);
        auto completion = winrt::make_self<Activation>(state);
        winrt::com_ptr<IActivateAudioInterfaceAsyncOperation> operation;
        winrt::check_hresult(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            __uuidof(IAudioClient),&value,completion.get(),operation.put()));
        HANDLE activationEvents[] = {state->stop,state->activated};
        DWORD activation = WaitForMultipleObjects(2,activationEvents,FALSE,10000);
        if (activation == WAIT_OBJECT_0) throw winrt::hresult_canceled();
        if (activation != WAIT_OBJECT_0 + 1) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
        winrt::check_hresult(state->activationResult.load());
        { std::lock_guard lock(state->mutex); client = std::move(state->client); }
        if (!client || state->cancelled) throw winrt::hresult_canceled();
        WAVEFORMATEX format{WAVE_FORMAT_PCM,2,Rate,Rate * 4,4,16,0};
        winrt::check_hresult(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
            0,0,&format,nullptr));
        winrt::check_hresult(client->SetEventHandle(state->ready));
        winrt::com_ptr<IAudioCaptureClient> reader;
        winrt::check_hresult(client->GetService(__uuidof(IAudioCaptureClient),reader.put_void()));
        winrt::check_hresult(client->Start());
        state->status = S_OK;
        Processor processor;
        HANDLE captureEvents[] = {state->stop,state->ready};
        while (!state->cancelled)
        {
            DWORD wait = WaitForMultipleObjects(2,captureEvents,FALSE,250);
            if (wait == WAIT_OBJECT_0) break;
            if (wait == WAIT_TIMEOUT)
            {
                std::lock_guard lock(state->mutex); state->bands.fill(0); continue;
            }
            if (wait != WAIT_OBJECT_0 + 1) winrt::throw_last_error();
            UINT32 packets = 0;
            winrt::check_hresult(reader->GetNextPacketSize(&packets));
            while (packets && !state->cancelled)
            {
                BYTE* data = nullptr; UINT32 frames = 0; DWORD flags = 0;
                winrt::check_hresult(reader->GetBuffer(&data,&frames,&flags,nullptr,nullptr));
                processor.Add(flags & AUDCLNT_BUFFERFLAGS_SILENT ? nullptr : reinterpret_cast<int16_t const*>(data),frames,*state);
                winrt::check_hresult(reader->ReleaseBuffer(frames));
                winrt::check_hresult(reader->GetNextPacketSize(&packets));
            }
        }
    }
    catch (...) { state->status = winrt::to_hresult(); }
    if (client) client->Stop();
    client = nullptr;
    state->cancelled = true;
    if (apartment) winrt::uninit_apartment();
}
struct Session { std::shared_ptr<CaptureState> state; };
}

extern "C" void* __stdcall StartLightSpectrum() noexcept
{
    try
    {
        auto state = std::make_shared<CaptureState>();
        if (!state->stop || !state->activated || !state->ready) return nullptr;
        auto session = std::make_unique<Session>(Session{state});
        DWORD process = GetCurrentProcessId();
        std::thread([state,process] { Capture(state,process); }).detach();
        return session.release();
    }
    catch (...) { return nullptr; }
}
extern "C" HRESULT __stdcall ReadLightSpectrum(void* handle, float* levels, int count) noexcept
{
    if (!handle || !levels || count != Bands) return E_INVALIDARG;
    auto& state = *static_cast<Session*>(handle)->state;
    std::lock_guard lock(state.mutex);
    std::copy(state.bands.begin(),state.bands.end(),levels);
    return state.status;
}
extern "C" void __stdcall StopLightSpectrum(void* handle) noexcept
{
    if (!handle) return;
    std::unique_ptr<Session> session(static_cast<Session*>(handle));
    session->state->cancelled = true;
    SetEvent(session->state->stop);
}
