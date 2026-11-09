#include "UsbPrintStream.hpp"
#include "libcapt/BasicCaptPrinter.hpp"
#include "libcapt/Compression/ScoaStreambuf.hpp"
#include "libcapt/Compression/Decoder/DecoderStreambuf.hpp"
#include <atomic>
#include <chrono>
#include <csignal>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <random>
#include <sstream>
#include <string_view>
#include <thread>
using namespace Capt;
using Clock=std::chrono::steady_clock;
std::atomic<bool> stopped=false;
std::filesystem::path cancelFile;
bool cancelRequested() noexcept {
    if (stopped.load()) return true;
    if (cancelFile.empty()) return false;
    static auto nextCheck=Clock::time_point::min();
    if (Clock::now()<nextCheck) return false;
    nextCheck=Clock::now()+std::chrono::milliseconds(100);
    std::error_code error;
    if (std::filesystem::exists(cancelFile,error)) stopped=true;
    return stopped.load();
}
struct JobStopToken {
    Clock::time_point deadline=Clock::now()+std::chrono::seconds(90);
    bool stop_requested() const noexcept { return cancelRequested() || Clock::now()>deadline; }
};
struct Page { unsigned width,height; std::string data; };
bool finishedBeforeEmptyTray(const ExtendedStatus& s,unsigned pageCount) noexcept {
    // Captured LBP-1120 behavior: the last sheet can empty the tray while SAFE_TIMER
    // is still set. The printed counters already confirm that the page succeeded.
    return (s.Engine&EngineReadyStatus::NO_PRINT_PAPER)!=0
        && s.Start==pageCount && s.Printing==pageCount && s.Printed==pageCount
        && (s.Aux&AuxStatus::PAPER_DELIVERY)==0
        && !s.FatalError() && !s.VideoDataError() && !s.Rejected() && !s.Misprint();
}

std::string compress(const std::string& raster,unsigned lineSize,unsigned lines) {
    std::stringbuf source(raster,std::ios::in);
    Compression::ScoaStreambuf codec(source,lineSize,lines);
    std::istream input(&codec);
    return {std::istreambuf_iterator<char>(input),{}};
}
void selftest() {
    std::mt19937 rng(1120);
    for (int pattern=0;pattern<4;++pattern) {
        constexpr unsigned line=600, rows=41;
        std::string raster(line*rows,'\0');
        for (size_t i=0;i<raster.size();++i) {
            if (pattern==1) raster[i]=static_cast<char>(0xff);
            if (pattern==2) raster[i]=static_cast<char>((i%line)%17 ? 0 : 0xff);
            if (pattern==3) raster[i]=static_cast<char>(rng());
        }
        auto encoded=compress(raster,line,rows);
        std::stringbuf source(encoded,std::ios::in);
        Compression::DecoderStreambuf decoder(source,line);
        std::istream input(&decoder);
        std::string decoded{std::istreambuf_iterator<char>(input),{}};
        if (decoded!=raster) throw std::runtime_error("SCoA compression round-trip mismatch");
        std::cout<<"Compression round-trip "<<pattern<<": PASS ("<<raster.size()<<" -> "<<encoded.size()<<" bytes)\n";
    }
    ExtendedStatus end{};
    end.Basic=static_cast<BasicStatus>(BasicStatus::NOT_READY|BasicStatus::OFFLINE);
    end.Aux=AuxStatus::SAFE_TIMER;
    end.Engine=EngineReadyStatus::NO_PRINT_PAPER;
    end.Start=end.Printing=end.Printed=2;
    if (!finishedBeforeEmptyTray(end,2)) throw std::runtime_error("Completed job incorrectly failed on empty tray");
    end.Printed=1;
    if (finishedBeforeEmptyTray(end,2)) throw std::runtime_error("Unfinished page incorrectly accepted on empty tray");
    std::cout<<"Captured empty-tray completion regression: PASS\n";
}
std::string token(std::istream& in) {
    for (;;) {
        in>>std::ws;
        if (in.peek()!='#') break;
        std::string ignored; std::getline(in,ignored);
    }
    std::string result;
    if (!(in>>result)) throw std::runtime_error("Truncated PBM header");
    return result;
}
std::vector<Page> load(const std::filesystem::path& path) {
    std::ifstream in(path,std::ios::binary);
    if (!in) throw std::runtime_error("Cannot open PBM file");
    std::vector<Page> pages;
    while (in.peek()!=EOF) {
        if (pages.size()>=100) throw std::runtime_error("Limit is 100 pages per job");
        if (token(in)!="P4") throw std::runtime_error("Only binary P4 PBM files are supported");
        unsigned w=static_cast<unsigned>(std::stoul(token(in))),h=static_cast<unsigned>(std::stoul(token(in)));
        if (!w || w>4800 || w%8 || !h || h>6800) throw std::runtime_error("PBM must be <=4800x6800, width divisible by 8 (A4, 600 dpi)");
        char delim; in.get(delim);
        if (!std::isspace(static_cast<unsigned char>(delim))) throw std::runtime_error("Invalid PBM separator");
        if (delim=='\r' && in.peek()=='\n') in.get();
        std::string raster(static_cast<size_t>(w/8)*h,'\0');
        in.read(raster.data(),raster.size());
        if (in.gcount()!=static_cast<std::streamsize>(raster.size())) throw std::runtime_error("Truncated PBM raster");
        pages.push_back({w,h,compress(raster,w/8,h)});
    }
    if (pages.empty()) throw std::runtime_error("PBM contains no pages");
    return pages;
}
std::string errorText(const ExtendedStatus& s) {
    if (s.Engine&EngineReadyStatus::DOOR_OPEN) return "Printer cover is open";
    if (s.Engine&EngineReadyStatus::NO_CARTRIDGE) return "Toner cartridge is missing";
    if (s.Engine&EngineReadyStatus::NO_PRINT_PAPER) return "Load A4 paper";
    if (s.Engine&EngineReadyStatus::JAM) return "Paper jam";
    if (s.FatalError()) return "Printer reported a fatal error";
    return "Printer is not ready";
}
void status(const ExtendedStatus& s) {
    std::printf("Ready=%s Online=%s Printing=%s Engine=0x%04X Controller=0x%02X Start=%u PrintingPage=%u Printed=%u\n",
        s.Ready()?"yes":"no",s.Online()?"yes":"no",s.IsPrinting()?"yes":"no",s.Engine,s.Controller,s.Start,s.Printing,s.Printed);
}
void printPages(const std::vector<Page>& pages) {
    CaptDeviceLock lock;
    auto path=UsbPrintStream::find();
    if (path.empty()) throw std::runtime_error("Canon LBP-1120 is not connected");
    UsbPrintStream transport(path);
    std::iostream stream(&transport);
    stream.exceptions(std::ios::badbit|std::ios::failbit);
    BasicCaptPrinter<JobStopToken> printer(stream);
    auto initial=printer.GetStatus();
    status(initial);
    if (initial.UnitReserved() || initial.Online() || initial.IsPrinting()) throw std::runtime_error("Printer is busy; another job owns it");
    if (!initial.Ready() && !(initial.Engine&EngineReadyStatus::WAITING)) throw std::runtime_error(errorText(initial));
    bool reserved=false;
    try {
        printer.ReserveUnit(); reserved=true;
        printer.ClearError();
        for (size_t i=0;i<pages.size();++i) {
            if (cancelRequested()) throw std::runtime_error("Printing cancelled");
            const auto deadline=Clock::now()+std::chrono::seconds(90);
            auto s=printer.GetStatus();
            while (!s.Ready()) {
                if (cancelRequested() || Clock::now()>deadline) throw std::runtime_error(errorText(s));
                if (s.Engine&(EngineReadyStatus::DOOR_OPEN|EngineReadyStatus::NO_CARTRIDGE|EngineReadyStatus::JAM|EngineReadyStatus::NO_PRINT_PAPER)) throw std::runtime_error(errorText(s));
                std::this_thread::sleep_for(std::chrono::milliseconds(250));
                s=printer.GetStatus();
            }
            if (!printer.GoOnline(static_cast<unsigned>(i))) throw std::runtime_error("Printer refused GoOnline");
            const auto& p=pages[i];
            PageParams params{};
            params.PaperSize=9; params.TonerDensity=0x3f;
            params.Resolution=ResolutionIdx::RES_600; params.SmoothEnable=true;
            params.MarginLeft=80; params.MarginTop=120;
            params.ImageLineSize=static_cast<uint16_t>(p.width/8); params.ImageLines=static_cast<uint16_t>(p.height);
            params.PaperWidth=4960; params.PaperHeight=7014;
            std::stringbuf image(p.data,std::ios::in);
            std::cout<<"Sending page "<<(i+1)<<" of "<<pages.size()<<" ("<<p.data.size()<<" compressed bytes)...\n";
            if (!printer.WriteVideoData(JobStopToken{},params,image)) throw std::runtime_error("Printer rejected image data; automatic retry is disabled");
            const auto printDeadline=Clock::now()+std::chrono::seconds(120);
            uint16_t lastEngine=0xffff,lastPrinted=0xffff,lastStart=0xffff;
            do {
                if (cancelRequested() || Clock::now()>printDeadline) throw std::runtime_error("Print completion timed out or cancelled");
                std::this_thread::sleep_for(std::chrono::milliseconds(500));
                s=printer.GetStatus();
                if (s.Engine!=lastEngine || s.Printed!=lastPrinted || s.Start!=lastStart) {
                    status(s); lastEngine=s.Engine; lastPrinted=s.Printed; lastStart=s.Start;
                }
                if (s.FatalError() || s.VideoDataError() || s.Rejected() || s.Misprint()) { status(s); throw std::runtime_error("Printer reported a print error; no automatic retry"); }
                if (i+1==pages.size() && finishedBeforeEmptyTray(s,static_cast<unsigned>(i+1))) {
                    std::cout<<"Last page completed; tray is now empty.\n";
                    break;
                }
                if (s.Engine&(EngineReadyStatus::DOOR_OPEN|EngineReadyStatus::NO_CARTRIDGE|EngineReadyStatus::JAM|EngineReadyStatus::NO_PRINT_PAPER)) throw std::runtime_error(errorText(s));
            } while (s.IsPrinting());
            std::cout<<"Printer reports page "<<(i+1)<<" completed.\n";
        }
        printer.GoOffline();
        printer.ReleaseUnit(); reserved=false;
    } catch (...) {
        if (reserved) {
            try {
                stream.clear();
                printer.GoOffline();
                printer.ReleaseUnit();
            } catch (...) { std::cerr<<"Could not release printer; turn it off and on before retrying.\n"; }
        }
        throw;
    }
}
int wmain(int argc,wchar_t** argv) {
    std::setbuf(stdout,nullptr);
    std::signal(SIGINT,[](int){stopped=true;});
    static_assert(sizeof(void*)==8);
    try {
        if (argc==2 && std::wstring_view(argv[1])==L"--selftest") { selftest(); return 0; }
        if (argc==2 && std::wstring_view(argv[1])==L"--verify-pbm") throw std::runtime_error("Pass a PBM path after --verify-pbm");
        if (argc==3 && std::wstring_view(argv[1])==L"--verify-pbm") {
            auto pages=load(argv[2]);
            std::cout<<"Validated and compressed "<<pages.size()<<" PBM page(s), without contacting printer.\n";
            return 0;
        }
        if (argc==5 && std::wstring_view(argv[3])==L"--cancel-file") cancelFile=argv[4];
        if ((argc!=3 && !(argc==5 && !cancelFile.empty())) || std::wstring_view(argv[1])!=L"--print-pbm") {
            std::cout<<"Experimental LBP-1120 x64 direct printer (not a Windows printer driver).\n"
                       "Usage: capt-print --selftest | --verify-pbm FILE | --print-pbm FILE\n";
            return 2;
        }
        auto pages=load(argv[2]);
        printPages(pages);
        return 0;
    } catch (const std::exception& e) { std::cerr<<"ERROR: "<<e.what()<<'\n'; return 1; }
}
