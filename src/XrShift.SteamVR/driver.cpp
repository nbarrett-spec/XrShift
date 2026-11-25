// XrShift's SteamVR driver: makes XR glasses a seated, rotation-only VR headset.
//
// SteamVR draws the game into a window on the glasses (its "extended" display mode). Everything the driver knows
// comes from XrShift through one block of shared memory, "Local\XrShift.VrPose" (struct WireBlock below, defined
// once in src/XrShift.Scene/SteamVr.cs as VrWire and described in docs/VR.md): the head's orientation and turn
// rate, the glasses' place on the desktop, whether they are in their side-by-side 3D mode, their field of view,
// and whether VR mode is on. Without that block (XrShift not running, or not in VR mode) the driver adds nothing,
// so SteamVR is free to use any other headset.
//
// Licence: MIT (see LICENSE at the root). Uses Valve's OpenVR driver header (BSD-3, third_party/openvr).

#include <windows.h>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <thread>
#include "third_party/openvr/openvr_driver.h"

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

using namespace vr;

namespace
{
    // ---- the wire: what XrShift writes (VrWire in src/XrShift.Scene/SteamVr.cs) -------------------------------

    const wchar_t *const WireName = L"Local\\XrShift.VrPose";
    const uint32_t WireMagic = 0x56535258; // "XRSV"
    const uint32_t WireVersion = 1;
    const int32_t CommandRun = 0;
    const int32_t CommandQuitSteamVr = 1;

    struct WireBlock
    {
        uint32_t sequence;          // odd while XrShift is writing
        uint32_t magic;
        uint32_t version;
        uint32_t size;
        int64_t sampleTicks;        // QueryPerformanceCounter when the head was measured
        double rotation[4];         // w, x, y, z: head to world, X right, Y up, Z back (SteamVR's own axes)
        double angularVelocity[3];  // rad/s in the same world
        int32_t windowX;
        int32_t windowY;
        int32_t windowWidth;
        int32_t windowHeight;
        int32_t sideBySide;         // 1: left half for the left eye, right half for the right
        float refreshHz;
        float fovDeg;               // horizontal, of one eye's whole picture
        float ipdMeters;
        float vsyncToPhotons;       // seconds
        int32_t command;            // CommandRun or CommandQuitSteamVr
        int64_t driverTicks;        // written by this driver only: when it last read the block
    };
    // Pinned against VrWire by tests/XrShift.Scene.Tests/SteamVrTests.cs: change both together.
    static_assert(sizeof(WireBlock) == 128, "WireBlock size");
    static_assert(offsetof(WireBlock, sequence) == 0, "sequence");
    static_assert(offsetof(WireBlock, magic) == 4, "magic");
    static_assert(offsetof(WireBlock, version) == 8, "version");
    static_assert(offsetof(WireBlock, size) == 12, "size");
    static_assert(offsetof(WireBlock, sampleTicks) == 16, "sampleTicks");
    static_assert(offsetof(WireBlock, rotation) == 24, "rotation");
    static_assert(offsetof(WireBlock, angularVelocity) == 56, "angularVelocity");
    static_assert(offsetof(WireBlock, windowX) == 80, "windowX");
    static_assert(offsetof(WireBlock, windowY) == 84, "windowY");
    static_assert(offsetof(WireBlock, windowWidth) == 88, "windowWidth");
    static_assert(offsetof(WireBlock, windowHeight) == 92, "windowHeight");
    static_assert(offsetof(WireBlock, sideBySide) == 96, "sideBySide");
    static_assert(offsetof(WireBlock, refreshHz) == 100, "refreshHz");
    static_assert(offsetof(WireBlock, fovDeg) == 104, "fovDeg");
    static_assert(offsetof(WireBlock, ipdMeters) == 108, "ipdMeters");
    static_assert(offsetof(WireBlock, vsyncToPhotons) == 112, "vsyncToPhotons");
    static_assert(offsetof(WireBlock, command) == 116, "command");
    static_assert(offsetof(WireBlock, driverTicks) == 120, "driverTicks");

    // One consistent copy of the block.
    struct Wire
    {
        int64_t sampleTicks = 0;
        HmdQuaternion_t rotation{ 1, 0, 0, 0 };
        double angularVelocity[3] = { 0, 0, 0 };
        int32_t windowX = 0, windowY = 0, windowWidth = 0, windowHeight = 0;
        bool sideBySide = false;
        float refreshHz = 0, fovDeg = 0, ipdMeters = 0, vsyncToPhotons = 0;
        int32_t command = CommandQuitSteamVr;
    };

    enum class WireState { Missing, WrongVersion, Busy, Ok };

    void Log(const char *format, ...)
    {
        char line[512];
        va_list args;
        va_start(args, format);
        vsnprintf(line, sizeof(line), format, args);
        va_end(args);
        if (IVRDriverLog *log = VRDriverLog()) log->Log(line);
    }

    // Reads the block. Used by one thread at a time: Init's probe, then only the pose thread.
    class WireReader
    {
    public:
        WireReader() = default;
        WireReader(const WireReader &) = delete;
        WireReader &operator=(const WireReader &) = delete;
        ~WireReader() { Close(); }

        WireState Read(Wire &out)
        {
            if (!_view && !Open()) return WireState::Missing;
            // Volatile: every field is read from memory each time, with acquire order (MSVC's /volatile:ms, the x64 default).
            const volatile WireBlock *b = _view;
            if (b->magic != WireMagic || b->version != WireVersion || b->size != sizeof(WireBlock)) return WireState::WrongVersion;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                uint32_t before = b->sequence;
                if (before & 1) { YieldProcessor(); continue; }
                MemoryBarrier();
                Wire w;
                w.sampleTicks = b->sampleTicks;
                w.rotation.w = b->rotation[0];
                w.rotation.x = b->rotation[1];
                w.rotation.y = b->rotation[2];
                w.rotation.z = b->rotation[3];
                for (int i = 0; i < 3; i++) w.angularVelocity[i] = b->angularVelocity[i];
                w.windowX = b->windowX;
                w.windowY = b->windowY;
                w.windowWidth = b->windowWidth;
                w.windowHeight = b->windowHeight;
                w.sideBySide = b->sideBySide != 0;
                w.refreshHz = b->refreshHz;
                w.fovDeg = b->fovDeg;
                w.ipdMeters = b->ipdMeters;
                w.vsyncToPhotons = b->vsyncToPhotons;
                w.command = b->command;
                MemoryBarrier();
                if (b->sequence != before) continue;
                out = w;
                return WireState::Ok;
            }
            return WireState::Busy;
        }

        // Tells XrShift the driver is alive inside SteamVR (it shows on the Games page and decides whether
        // starting VR again needs SteamVR restarted). Its own 8 bytes, outside the sequence lock.
        void MarkSeen()
        {
            if (!_view) return;
            LARGE_INTEGER now;
            QueryPerformanceCounter(&now);
            _view->driverTicks = now.QuadPart;
        }

    private:
        bool Open()
        {
            _mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, WireName);
            if (!_mapping) return false;
            _view = static_cast<volatile WireBlock *>(MapViewOfFile(_mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(WireBlock)));
            if (!_view) { CloseHandle(_mapping); _mapping = nullptr; return false; }
            return true;
        }

        void Close()
        {
            if (_view) UnmapViewOfFile(const_cast<WireBlock *>(_view));
            if (_mapping) CloseHandle(_mapping);
            _view = nullptr;
            _mapping = nullptr;
        }

        HANDLE _mapping = nullptr;
        volatile WireBlock *_view = nullptr;
    };

    // ---- the headset ------------------------------------------------------------------------------------

    const char *const Serial = "XRSHIFT-GLASSES";
    const double StaleSeconds = 0.25; // no reading for this long: XrShift has stopped (or the glasses are unplugged)

    // Rotation only, so the head turns about a fixed neck. The pivot sits where a seated adult's neck is; the eyes
    // are above and in front of it (the usual neck model figures), which puts them 1.2 m above the floor looking
    // straight ahead, and lets them swing a few centimetres as the head turns and nods, as real eyes do.
    const double EyeHeight = 1.2;
    const double NeckToEyesUp = 0.075;
    const double NeckToEyesForward = 0.0805;

    class Glasses : public ITrackedDeviceServerDriver, public IVRDisplayComponent
    {
    public:
        explicit Glasses(const Wire &display) : _d(display) {}
        Glasses(const Glasses &) = delete;
        Glasses &operator=(const Glasses &) = delete;
        ~Glasses() { StopPoses(); }

        EVRInitError Activate(uint32_t id) override
        {
            StopPoses(); // a second Activate must not overwrite a running thread (std::terminate)
            _id = id;
            PropertyContainerHandle_t p = VRProperties()->TrackedDeviceToPropertyContainer(id);
            VRProperties()->SetStringProperty(p, Prop_ModelNumber_String, "XrShift glasses");
            VRProperties()->SetStringProperty(p, Prop_SerialNumber_String, Serial);
            VRProperties()->SetStringProperty(p, Prop_ManufacturerName_String, "XrShift");
            VRProperties()->SetStringProperty(p, Prop_TrackingSystemName_String, "xrshift");
            VRProperties()->SetStringProperty(p, Prop_RenderModelName_String, "generic_hmd");
            VRProperties()->SetFloatProperty(p, Prop_UserIpdMeters_Float, _d.ipdMeters);
            VRProperties()->SetFloatProperty(p, Prop_UserHeadToEyeDepthMeters_Float, 0.f);
            VRProperties()->SetFloatProperty(p, Prop_DisplayFrequency_Float, _d.refreshHz);
            VRProperties()->SetFloatProperty(p, Prop_SecondsFromVsyncToPhotons_Float, _d.vsyncToPhotons);
            VRProperties()->SetUint64Property(p, Prop_CurrentUniverseId_Uint64, 0x58525348ull);
            VRProperties()->SetBoolProperty(p, Prop_IsOnDesktop_Bool, true);
            VRProperties()->SetBoolProperty(p, Prop_DisplayDebugMode_Bool, false);
            VRProperties()->SetBoolProperty(p, Prop_HasDisplayComponent_Bool, true);
            VRProperties()->SetBoolProperty(p, Prop_WillDriftInYaw_Bool, true);
            VRProperties()->SetBoolProperty(p, Prop_DeviceProvidesBatteryStatus_Bool, false);

            _running.store(true);
            _poses = std::thread(&Glasses::PoseLoop, this);
            Log("XrShift: glasses active, window %d,%d %dx%d, %s, %.0f Hz, view angle %.1f degrees.", _d.windowX, _d.windowY,
                _d.windowWidth, _d.windowHeight, _d.sideBySide ? "3D side by side" : "flat", _d.refreshHz, _d.fovDeg);
            return VRInitError_None;
        }

        void Deactivate() override
        {
            StopPoses();
            _id = k_unTrackedDeviceIndexInvalid;
        }

        void EnterStandby() override {}
        void DebugRequest(const char *, char *response, uint32_t size) override { if (size) response[0] = 0; }

        void *GetComponent(const char *name) override
        {
            return std::strcmp(name, IVRDisplayComponent_Version) == 0 ? static_cast<IVRDisplayComponent *>(this) : nullptr;
        }

        // The header says SteamVR never calls this; poses go out from PoseLoop. A not-yet-valid pose, to be safe.
        DriverPose_t GetPose() override
        {
            DriverPose_t pose{};
            pose.qWorldFromDriverRotation.w = 1;
            pose.qDriverFromHeadRotation.w = 1;
            pose.qRotation.w = 1;
            pose.deviceIsConnected = true;
            pose.result = TrackingResult_Uninitialized;
            return pose;
        }

        // On SteamVR's main loop: never blocks. Events are drained, and XrShift's request to close SteamVR is passed on.
        void RunFrame()
        {
            VREvent_t event{};
            while (VRServerDriverHost()->PollNextEvent(&event, sizeof(event))) {}
            int32_t command = _command.load();
            if (command == CommandRun) _quitSent = false;
            else if (command == CommandQuitSteamVr && !_quitSent && _id != k_unTrackedDeviceIndexInvalid)
            {
                _quitSent = true;
                Log("XrShift: VR mode ended in XrShift; asking SteamVR to close.");
                VREvent_Data_t data{};
                VRServerDriverHost()->VendorSpecificEvent(_id, VREvent_DriverRequestedQuit, data, 0);
            }
        }

        void StopPoses()
        {
            _running.store(false);
            if (_poses.joinable()) _poses.join();
        }

        // ---- the display: a window over the glasses' part of the desktop ----

        void GetWindowBounds(int32_t *x, int32_t *y, uint32_t *w, uint32_t *h) override
        {
            *x = _d.windowX;
            *y = _d.windowY;
            *w = uint32_t(_d.windowWidth);
            *h = uint32_t(_d.windowHeight);
        }

        bool IsDisplayOnDesktop() override { return true; }
        bool IsDisplayRealDisplay() override { return true; }

        void GetRecommendedRenderTargetSize(uint32_t *w, uint32_t *h) override
        {
            // One eye's own pixels: there is no lens distortion to stretch the picture, so more is only cost.
            // SteamVR's render resolution setting still scales it.
            *w = EyeWidth();
            *h = uint32_t(_d.windowHeight);
        }

        void GetEyeOutputViewport(EVREye eye, uint32_t *x, uint32_t *y, uint32_t *w, uint32_t *h) override
        {
            // 3D: left half, right half. Flat: both eyes on the whole screen (they see the same, IPD 0).
            *x = (_d.sideBySide && eye == Eye_Right) ? EyeWidth() : 0;
            *y = 0;
            *w = EyeWidth();
            *h = uint32_t(_d.windowHeight);
        }

        void GetProjectionRaw(EVREye, float *left, float *right, float *top, float *bottom) override
        {
            // Each eye looks straight ahead through the middle of its picture. Tangents; OpenVR has top negative.
            float tanX = std::tan(_d.fovDeg * 0.5f * 3.14159265f / 180.f);
            float tanY = tanX * float(_d.windowHeight) / float(EyeWidth());
            *left = -tanX;
            *right = tanX;
            *top = -tanY;
            *bottom = tanY;
        }

        DistortionCoordinates_t ComputeDistortion(EVREye, float u, float v) override
        {
            // The glasses' optics need no correction from the PC.
            DistortionCoordinates_t c{};
            c.rfRed[0] = c.rfGreen[0] = c.rfBlue[0] = u;
            c.rfRed[1] = c.rfGreen[1] = c.rfBlue[1] = v;
            return c;
        }

        bool ComputeInverseDistortion(HmdVector2_t *result, EVREye, uint32_t, float u, float v) override
        {
            result->v[0] = u;
            result->v[1] = v;
            return true;
        }

    private:
        uint32_t EyeWidth() const { return uint32_t(_d.sideBySide ? _d.windowWidth / 2 : _d.windowWidth); }

        // Hands SteamVR a pose every 2 ms on its own thread, as Valve's sample driver does: SteamVR's main loop
        // does not run at a steady rate, and a pose should never wait for it.
        void PoseLoop()
        {
            // A high-resolution timer: a plain Sleep can last 15 ms on Windows.
            HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            LARGE_INTEGER frequency;
            QueryPerformanceFrequency(&frequency);
            while (_running.load() && !VRServerDriverHost()->IsExiting())
            {
                DriverPose_t pose = MakePose(double(frequency.QuadPart));
                VRServerDriverHost()->TrackedDevicePoseUpdated(_id, pose, sizeof(DriverPose_t));
                LARGE_INTEGER due;
                due.QuadPart = -20000; // 2 ms, in 100 ns steps, relative
                if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, 50);
                else Sleep(1);
            }
            if (timer) CloseHandle(timer);
        }

        DriverPose_t MakePose(double frequency)
        {
            DriverPose_t pose{};
            pose.qWorldFromDriverRotation.w = 1;
            pose.qDriverFromHeadRotation.w = 1;
            // The driver's reference point is the neck; the head (between the eyes) is offset from it in the
            // head's own frame, so SteamVR swings the eyes round the neck, also when it predicts.
            pose.vecPosition[0] = 0;
            pose.vecPosition[1] = EyeHeight - NeckToEyesUp;
            pose.vecPosition[2] = NeckToEyesForward;
            pose.vecDriverFromHeadTranslation[1] = NeckToEyesUp;
            pose.vecDriverFromHeadTranslation[2] = -NeckToEyesForward;
            pose.qRotation = _lastGood;
            pose.deviceIsConnected = true;
            pose.willDriftInYaw = true;
            pose.shouldApplyHeadModel = false; // the neck model is above
            pose.poseIsValid = false;
            pose.result = TrackingResult_Uninitialized;

            Wire w;
            WireState state = _reader.Read(w);
            if (state != WireState::Ok) return pose;
            _reader.MarkSeen();
            _command.store(w.command);
            pose.result = TrackingResult_Running_OutOfRange;

            LARGE_INTEGER now;
            QueryPerformanceCounter(&now);
            double age = double(now.QuadPart - w.sampleTicks) / frequency;
            const HmdQuaternion_t &q = w.rotation;
            double norm = q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z;
            // A comparison with NaN is false, so a broken number fails here too.
            bool fresh = age > -0.01 && age < StaleSeconds && norm > 0.9 && norm < 1.1 && w.command == CommandRun;
            if (!fresh) return pose; // say so rather than freeze the picture on a stale direction

            _lastGood = q;
            pose.qRotation = q;
            for (int i = 0; i < 3; i++) pose.vecAngularVelocity[i] = std::isfinite(w.angularVelocity[i]) ? w.angularVelocity[i] : 0.0;
            // The reading is this old already; SteamVR predicts from then to when the frame is seen.
            pose.poseTimeOffset = -(age > 0 ? age : 0);
            pose.poseIsValid = true;
            pose.result = TrackingResult_Running_OK;
            return pose;
        }

        const Wire _d; // the display as it was when SteamVR started: SteamVR reads it once
        uint32_t _id = k_unTrackedDeviceIndexInvalid;
        WireReader _reader;
        std::thread _poses;
        std::atomic<bool> _running{ false };
        std::atomic<int32_t> _command{ CommandRun };
        bool _quitSent = false;
        HmdQuaternion_t _lastGood{ 1, 0, 0, 0 };
    };

    // ---- the provider SteamVR loads -----------------------------------------------------------------

    class Provider : public IServerTrackedDeviceProvider
    {
    public:
        EVRInitError Init(IVRDriverContext *context) override
        {
            VR_INIT_SERVER_DRIVER_CONTEXT(context);

            // Only while XrShift is in VR mode: otherwise SteamVR should not see these glasses at all.
            Wire w;
            WireReader probe;
            switch (probe.Read(w))
            {
            case WireState::Missing:
                Log("XrShift: XrShift is not in VR mode (no shared block), so no glasses are added.");
                return VRInitError_Init_HmdNotFound;
            case WireState::WrongVersion:
                Log("XrShift: the shared block is from another version of XrShift (this driver reads version %u). Use the driver that came with the running XrShift.", WireVersion);
                return VRInitError_Init_HmdNotFound;
            case WireState::Busy:
                Log("XrShift: could not read the shared block; no glasses added.");
                return VRInitError_Init_HmdNotFound;
            case WireState::Ok:
                break;
            }
            if (w.command != CommandRun)
            {
                Log("XrShift: VR mode is off in XrShift, so no glasses are added.");
                return VRInitError_Init_HmdNotFound;
            }
            if (w.windowWidth <= 0 || w.windowHeight <= 0 || w.refreshHz <= 1 || w.fovDeg <= 10 || w.fovDeg >= 150)
            {
                Log("XrShift: the glasses' display from XrShift makes no sense (%dx%d, %.1f Hz, %.1f degrees); no glasses added.",
                    w.windowWidth, w.windowHeight, w.refreshHz, w.fovDeg);
                return VRInitError_Init_HmdNotFound;
            }

            _glasses = new Glasses(w);
            if (!VRServerDriverHost()->TrackedDeviceAdded(Serial, TrackedDeviceClass_HMD, _glasses))
            {
                Log("XrShift: SteamVR did not take the glasses (another headset may be in use).");
                delete _glasses;
                _glasses = nullptr;
                return VRInitError_Driver_Failed;
            }
            return VRInitError_None;
        }

        void Cleanup() override
        {
            if (_glasses) _glasses->StopPoses();
            delete _glasses;
            _glasses = nullptr;
            VR_CLEANUP_SERVER_DRIVER_CONTEXT();
        }

        const char *const *GetInterfaceVersions() override { return k_InterfaceVersions; }
        void RunFrame() override { if (_glasses) _glasses->RunFrame(); }
        bool ShouldBlockStandbyMode() override { return false; }
        void EnterStandby() override {}
        void LeaveStandby() override {}

    private:
        Glasses *_glasses = nullptr;
    };

    Provider g_provider;
}

extern "C" __declspec(dllexport) void *HmdDriverFactory(const char *interfaceName, int *returnCode)
{
    if (std::strcmp(interfaceName, IServerTrackedDeviceProvider_Version) == 0) return &g_provider;
    if (returnCode) *returnCode = VRInitError_Init_InterfaceNotFound;
    return nullptr;
}
