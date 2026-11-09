#pragma once
#define NOMINMAX
#include <windows.h>
#include <setupapi.h>
#include <algorithm>
#include <array>
#include <cwctype>
#include <stdexcept>
#include <streambuf>
#include <string>
#include <system_error>
#include <vector>

class CaptDeviceLock {
    HANDLE mutex=nullptr;
public:
    CaptDeviceLock() {
        mutex=CreateMutexW(nullptr,FALSE,L"Local\\CanonLBP1120DirectPrintDevice");
        if (!mutex) throw std::system_error(GetLastError(),std::system_category(),"Create printer mutex");
        DWORD result=WaitForSingleObject(mutex,0);
        if (result!=WAIT_OBJECT_0 && result!=WAIT_ABANDONED) {
            CloseHandle(mutex); mutex=nullptr;
            throw std::runtime_error("Another LBP-1120 operation is running; wait for it to finish");
        }
    }
    ~CaptDeviceLock() { if (mutex) {ReleaseMutex(mutex);CloseHandle(mutex);} }
    CaptDeviceLock(const CaptDeviceLock&)=delete;
    CaptDeviceLock& operator=(const CaptDeviceLock&)=delete;
};

// Native x64 transport using Microsoft's existing usbprint.sys. No driver replacement.
class UsbPrintStream : public std::streambuf {
    HANDLE device = INVALID_HANDLE_VALUE;
    std::array<char,4096> input{};
    std::vector<char> output;
    bool poisoned=false;
    static void fail(const char* what) { throw std::system_error(GetLastError(), std::system_category(), what); }
    DWORD transfer(bool writing, void* data, DWORD length) {
        if (poisoned) throw std::runtime_error("USB connection failed; reconnect printer before retrying");
        OVERLAPPED op{};
        op.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!op.hEvent) fail("CreateEvent");
        DWORD count = 0;
        BOOL ok = writing ? WriteFile(device, data, length, &count, &op)
                          : ReadFile(device, data, length, &count, &op);
        if (!ok && GetLastError() == ERROR_IO_PENDING) {
            DWORD wait = WaitForSingleObject(op.hEvent, 5000);
            if (wait != WAIT_OBJECT_0) {
                CancelIoEx(device, &op);
                GetOverlappedResult(device, &op, &count, TRUE);
                CloseHandle(op.hEvent);
                poisoned=true;
                throw std::runtime_error("USB I/O timed out after 5 seconds; operation cancelled");
            }
            ok = GetOverlappedResult(device, &op, &count, FALSE);
        }
        DWORD error = ok ? ERROR_SUCCESS : GetLastError();
        CloseHandle(op.hEvent);
        if (error) { poisoned=true; throw std::system_error(error, std::system_category(), writing ? "USB write" : "USB read"); }
        return count;
    }
protected:
    int_type overflow(int_type ch) override {
        if (!traits_type::eq_int_type(ch, traits_type::eof())) output.push_back(traits_type::to_char_type(ch));
        return traits_type::not_eof(ch);
    }
    std::streamsize xsputn(const char* data, std::streamsize n) override {
        if (n > 0) output.insert(output.end(), data, data + n);
        return n;
    }
    int sync() override {
        size_t pos = 0;
        while (pos < output.size()) {
            auto n = transfer(true, output.data()+pos, static_cast<DWORD>(output.size()-pos));
            if (!n) throw std::runtime_error("USB write returned zero bytes");
            pos += n;
        }
        output.clear();
        return 0;
    }
    int_type underflow() override {
        if (gptr() && gptr() < egptr()) return traits_type::to_int_type(*gptr());
        auto n = transfer(false, input.data(), static_cast<DWORD>(input.size()));
        if (!n) return traits_type::eof();
        setg(input.data(), input.data(), input.data()+n);
        return traits_type::to_int_type(*gptr());
    }
public:
    static std::wstring find() {
        const GUID guid = {0x28d78fad,0x5a12,0x11d1,{0xae,0x5b,0x00,0x00,0xf8,0x03,0xa8,0xc2}};
        HDEVINFO set = SetupDiGetClassDevsW(&guid,nullptr,nullptr,DIGCF_PRESENT|DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) fail("SetupDiGetClassDevs");
        std::wstring result;
        for (DWORD i=0;;++i) {
            SP_DEVICE_INTERFACE_DATA iface{}; iface.cbSize=sizeof(iface);
            if (!SetupDiEnumDeviceInterfaces(set,nullptr,&guid,i,&iface)) {
                DWORD err=GetLastError();
                SetupDiDestroyDeviceInfoList(set);
                if (err!=ERROR_NO_MORE_ITEMS) throw std::system_error(err,std::system_category(),"enumerate USB printers");
                return result;
            }
            DWORD size=0;
            SetupDiGetDeviceInterfaceDetailW(set,&iface,nullptr,0,&size,nullptr);
            std::vector<unsigned char> buffer(size);
            auto detail=reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(buffer.data());
            detail->cbSize=sizeof(*detail);
            if (!SetupDiGetDeviceInterfaceDetailW(set,&iface,detail,size,nullptr,nullptr)) continue;
            std::wstring path=detail->DevicePath,lower=path;
            std::transform(lower.begin(),lower.end(),lower.begin(),[](wchar_t c){return std::towlower(c);});
            if (lower.find(L"vid_04a9&pid_262b")!=std::wstring::npos) {
                if (!result.empty()) { SetupDiDestroyDeviceInfoList(set); throw std::runtime_error("Multiple LBP-1120 printers connected; selection required"); }
                result=path;
            }
        }
    }
    explicit UsbPrintStream(const std::wstring& path) {
        device=CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,
                           nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
        if (device==INVALID_HANDLE_VALUE) fail("Open Canon USB printer");
    }
    ~UsbPrintStream() override { if (device!=INVALID_HANDLE_VALUE) CloseHandle(device); }
    UsbPrintStream(const UsbPrintStream&)=delete;
    UsbPrintStream& operator=(const UsbPrintStream&)=delete;
};
