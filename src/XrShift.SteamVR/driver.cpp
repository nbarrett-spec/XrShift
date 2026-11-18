// XrShift's SteamVR driver: makes XR glasses a seated, rotation-only VR headset.
//
// SteamVR draws the game into a window on the glasses (its "extended" display mode). The head's direction
// comes from XrShift, which already reads and fuses the glasses' motion sensor: XrShift publishes the latest
// orientation in a small shared-memory block ("Local\XrShift.VrPose"), and this driver hands it to SteamVR
// every frame. Where the glasses sit on the desktop, whether they are in their side-by-side 3D mode and their
// field of view come from %LOCALAPPDATA%\XrShift\vr.txt, written by XrShift when VR mode starts.
//
// Licence: MIT (see LICENSE at the root). Uses Valve's OpenVR driver header (BSD-3, third_party/openvr).

#include <windows.h>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include "third_party/openvr/openvr_driver.h"

using namespace vr;

namespace
{
    // ---- what XrShift tells the driver -------------------------------------------------------------

    struct Settings
    {
        int x = 1920, y = 0, width = 1920, height = 1080; // the glasses' place on the Windows desktop
        bool stereo = false;                             // side-by-side 3D: each eye gets half the width
        float hz = 60.f;
        float fovDeg = 50.7f;                            // horizontal field of view of the whole picture (one eye)
    };

    Settings ReadSettings()
    {
        Settings s;
        char folder[MAX_PATH] = {};
        if (GetEnvironmentVariableA("LOCALAPPDATA", folder, MAX_PATH) == 0) return s;
        std::string path = std::string(folder) + "\\XrShift\\vr.txt";
        FILE *file = nullptr;
        if (fopen_s(&file, path.c_str(), "r") != 0 || file == nullptr) return s;
        int stereo = 0;
        Settings read;
        if (fscanf_s(file, "%d,%d,%d,%d,%d,%f,%f", &read.x, &read.y, &read.width, &read.height, &stereo, &read.hz, &read.fovDeg) == 7 &&
            read.width > 0 && read.height > 0 && read.hz > 1 && read.fovDeg > 10 && read.fovDeg < 150)
        {
            s = read;
            s.stereo = stereo != 0;
        }
        fclose(file);
        return s;
    }

    // The block XrShift writes: a sequence number that is odd while a write is under way, then the pose.
    struct SharedPose
    {
        volatile LONG sequence;
        LONG magic;      // 'XRSV'
        double w, x, y, z;
        LONGLONG written; // QueryPerformanceCounter ticks
    };
    const LONG Magic = 0x56535258;

    class PoseReader
    {
    public:
        ~PoseReader() { Close(); }

        bool Read(HmdQuaternion_t &q, LONGLONG &written)
        {
            if (!_view && !Open()) return false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                LONG before = _view->sequence;
                if (before & 1) { YieldProcessor(); continue; }
                MemoryBarrier();
                if (_view->magic != Magic) return false;
                HmdQuaternion_t copy{ _view->w, _view->x, _view->y, _view->z };
                LONGLONG when = _view->written;
                MemoryBarrier();
                if (_view->sequence != before) continue;
                q = copy;
                written = when;
                return true;
            }
            return false;
        }

    private:
        bool Open()
        {
            _mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\XrShift.VrPose");
            if (!_mapping) return false;
            _view = static_cast<SharedPose *>(MapViewOfFile(_mapping, FILE_MAP_READ, 0, 0, sizeof(SharedPose)));
            if (!_view) { CloseHandle(_mapping); _mapping = nullptr; return false; }
            return true;
        }

        void Close()
        {
            if (_view) UnmapViewOfFile(const_cast<SharedPose *>(_view));
            if (_mapping) CloseHandle(_mapping);
            _view = nullptr;
            _mapping = nullptr;
        }

        HANDLE _mapping = nullptr;
        const SharedPose *_view = nullptr;
    };

    // ---- the headset ------------------------------------------------------------------------------------

    class Glasses : public ITrackedDeviceServerDriver, public IVRDisplayComponent
    {
    public:
        explicit Glasses(const Settings &s) : _s(s) {}

        EVRInitError Activate(uint32_t id) override
        {
            _id = id;
            PropertyContainerHandle_t p = VRProperties()->TrackedDeviceToPropertyContainer(id);
            VRProperties()->SetStringProperty(p, Prop_ModelNumber_String, "XrShift glasses");
            VRProperties()->SetStringProperty(p, Prop_ManufacturerName_String, "XrShift");
            VRProperties()->SetStringProperty(p, Prop_TrackingSystemName_String, "xrshift");
            VRProperties()->SetFloatProperty(p, Prop_UserIpdMeters_Float, 0.063f);
            VRProperties()->SetFloatProperty(p, Prop_UserHeadToEyeDepthMeters_Float, 0.f);
            VRProperties()->SetFloatProperty(p, Prop_DisplayFrequency_Float, _s.hz);
            VRProperties()->SetFloatProperty(p, Prop_SecondsFromVsyncToPhotons_Float, 0.008f);
            VRProperties()->SetUint64Property(p, Prop_CurrentUniverseId_Uint64, 0x58525348ull);
            VRProperties()->SetBoolProperty(p, Prop_IsOnDesktop_Bool, true);
            VRProperties()->SetBoolProperty(p, Prop_DisplayDebugMode_Bool, false);
            VRProperties()->SetBoolProperty(p, Prop_HasDisplayComponent_Bool, true);
            return VRInitError_None;
        }

        void Deactivate() override { _id = k_unTrackedDeviceIndexInvalid; }
        void EnterStandby() override {}
        void DebugRequest(const char *, char *response, uint32_t size) override { if (size) response[0] = 0; }

        void *GetComponent(const char *name) override
        {
            return std::strcmp(name, IVRDisplayComponent_Version) == 0 ? static_cast<IVRDisplayComponent *>(this) : nullptr;
        }

        DriverPose_t GetPose() override
        {
            DriverPose_t pose{};
            pose.qWorldFromDriverRotation.w = 1;
            pose.qDriverFromHeadRotation.w = 1;
            pose.qRotation.w = 1;
            pose.vecPosition[1] = 0; // seated: the head stays where the game puts it
            pose.deviceIsConnected = true;

            HmdQuaternion_t q;
            LONGLONG written = 0;
            if (_reader.Read(q, written))
            {
                LARGE_INTEGER now, frequency;
                QueryPerformanceCounter(&now);
                QueryPerformanceFrequency(&frequency);
                double age = double(now.QuadPart - written) / double(frequency.QuadPart);
                pose.qRotation = q;
                pose.poseIsValid = age < 0.5; // XrShift stopped publishing: say so rather than freeze
                pose.result = pose.poseIsValid ? TrackingResult_Running_OK : TrackingResult_Running_OutOfRange;
            }
            else
            {
                pose.poseIsValid = false;
                pose.result = TrackingResult_Uninitialized;
            }
            return pose;
        }

        void RunFrame()
        {
            if (_id != k_unTrackedDeviceIndexInvalid)
            {
                DriverPose_t pose = GetPose();
                VRServerDriverHost()->TrackedDevicePoseUpdated(_id, pose, sizeof(DriverPose_t));
            }
        }

        // ---- the display: a window on the glasses' part of the desktop ----

        void GetWindowBounds(int32_t *x, int32_t *y, uint32_t *w, uint32_t *h) override
        {
            *x = _s.x; *y = _s.y; *w = uint32_t(_s.width); *h = uint32_t(_s.height);
        }
        bool IsDisplayOnDesktop() override { return true; }
        bool IsDisplayRealDisplay() override { return true; }

        void GetRecommendedRenderTargetSize(uint32_t *w, uint32_t *h) override
        {
            // A little above the eye's own pixels, as headsets do, so the picture stays sharp after scaling.
            *w = uint32_t(EyeWidth() * 1.2f);
            *h = uint32_t(_s.height * 1.2f);
        }

        void GetEyeOutputViewport(EVREye eye, uint32_t *x, uint32_t *y, uint32_t *w, uint32_t *h) override
        {
            *y = 0;
            *h = uint32_t(_s.height);
            *w = uint32_t(EyeWidth());
            *x = (_s.stereo && eye == Eye_Right) ? uint32_t(EyeWidth()) : 0;
            if (!_s.stereo) *w = uint32_t(_s.width); // flat picture: both eyes see the same, whole width
        }

        void GetProjectionRaw(EVREye, float *left, float *right, float *top, float *bottom) override
        {
            float tanX = std::tan(_s.fovDeg * 0.5f * 3.14159265f / 180.f);
            float tanY = tanX * float(_s.height) / float(EyeWidth());
            *left = -tanX; *right = tanX;
            *top = -tanY; *bottom = tanY;
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
        float EyeWidth() const { return _s.stereo ? _s.width / 2.f : float(_s.width); }

        Settings _s;
        uint32_t _id = k_unTrackedDeviceIndexInvalid;
        PoseReader _reader;
    };

    // ---- the provider SteamVR loads -----------------------------------------------------------------

    class Provider : public IServerTrackedDeviceProvider
    {
    public:
        EVRInitError Init(IVRDriverContext *context) override
        {
            VR_INIT_SERVER_DRIVER_CONTEXT(context);
            _glasses = new Glasses(ReadSettings());
            VRServerDriverHost()->TrackedDeviceAdded("XRSHIFT-GLASSES", TrackedDeviceClass_HMD, _glasses);
            return VRInitError_None;
        }

        void Cleanup() override
        {
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
