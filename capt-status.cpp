#include "UsbPrintStream.hpp"
#include "libcapt/Protocol/Protocol.hpp"
#include <cstdio>
#include <iostream>

int main() {
    try {
        static_assert(sizeof(void*)==8,"This program must be built for x64");
        CaptDeviceLock lock;
        auto path=UsbPrintStream::find();
        if (path.empty()) throw std::runtime_error("Canon LBP-1120 USB device not found");
        std::cout << "Native Windows x64 CAPT diagnostic; status queries only.\n";
        UsbPrintStream transport(path);
        std::iostream stream(&transport);
        stream.exceptions(std::ios::badbit|std::ios::failbit);
        auto info=Capt::Protocol::PC_GET_PRINTER_INFO(stream);
        std::printf("Device=%u Type=%u Protocol=%u.%u BlockSize=%u Buffers=%u\n",
                    info.DeviceId,info.Type,info.VersionMajor,info.VersionMinor,info.BlockSize,info.Buffers);
        auto status=Capt::Protocol::PC_GET_EXTENDED_STATUS(stream);
        std::printf("Basic=0x%02X Aux=0x%02X Controller=0x%02X Engine=0x%04X\n",
                    status.Basic,status.Aux,status.Controller,status.Engine);
        std::printf("Ready=%s Online=%s Reserved=%s Printing=%s\n",status.Ready()?"yes":"no",
                    status.Online()?"yes":"no",status.UnitReserved()?"yes":"no",status.IsPrinting()?"yes":"no");
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "ERROR: " << e.what() << '\n';
        return 1;
    }
}
