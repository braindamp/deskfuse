// DeskFuse standalone WAN reachability probe. NOT a media transport or production auth.
// Uses only libdatachannel's C API and never loads the DeskFuse application.
#include <rtc/rtc.h>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <map>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {
using Clock = std::chrono::steady_clock;
std::string b64(const std::string &s) {
    static const char chars[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    for (size_t i = 0; i < s.size(); i += 3) {
        unsigned a = static_cast<unsigned char>(s[i]);
        unsigned b = i + 1 < s.size() ? static_cast<unsigned char>(s[i + 1]) : 0;
        unsigned c = i + 2 < s.size() ? static_cast<unsigned char>(s[i + 2]) : 0;
        out.push_back(chars[a >> 2]);
        out.push_back(chars[((a & 3) << 4) | (b >> 4)]);
        out.push_back(i + 1 < s.size() ? chars[((b & 15) << 2) | (c >> 6)] : '=');
        out.push_back(i + 2 < s.size() ? chars[c & 63] : '=');
    }
    return out;
}
int digit(char c) {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}
std::string from64(const std::string &s) {
    if (s.size() % 4) throw std::runtime_error("invalid base64 length");
    std::string out;
    for (size_t i = 0; i < s.size(); i += 4) {
        int a = digit(s[i]), b = digit(s[i + 1]);
        int c = s[i + 2] == '=' ? 0 : digit(s[i + 2]);
        int d = s[i + 3] == '=' ? 0 : digit(s[i + 3]);
        if (a < 0 || b < 0 || c < 0 || d < 0) throw std::runtime_error("bad base64");
        out.push_back(static_cast<char>((a << 2) | (b >> 4)));
        if (s[i + 2] != '=') out.push_back(static_cast<char>((b << 4) | (c >> 2)));
        if (s[i + 3] != '=') out.push_back(static_cast<char>((c << 6) | d));
    }
    return out;
}
struct Options {
    std::string role, signal, room;
    std::vector<std::string> ice;
    bool relayOnly = false;
    int seconds = 90;
};
Options parse(int argc, char **argv) {
    Options a;
    for (int i = 1; i < argc; ++i) {
        std::string flag = argv[i];
        if (flag == "--relay-only") { a.relayOnly = true; continue; }
        if (i + 1 >= argc) throw std::runtime_error("missing argument: " + flag);
        std::string v = argv[++i];
        if (flag == "--role") a.role = v;
        else if (flag == "--signal") a.signal = v;
        else if (flag == "--room") a.room = v;
        else if (flag == "--ice") a.ice.push_back(v);
        else if (flag == "--seconds") a.seconds = std::stoi(v);
        else throw std::runtime_error("unknown option " + flag);
    }
    if (a.role != "host" && a.role != "phone") throw std::runtime_error("invalid --role");
    if (a.room.size() < 8 || a.room.size() > 64 ||
        a.room.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-") != std::string::npos)
        throw std::runtime_error("invalid --room");
    if (a.signal.rfind("wss://", 0) != 0) {
        if (a.signal.rfind("ws://", 0) != 0)
            throw std::runtime_error("public signaling must use wss://");
        std::string authority = a.signal.substr(5);
        authority.resize(authority.find('/'));
        auto colon = authority.rfind(':');
        std::string hostname = authority.substr(0, colon);
        std::string port = colon == std::string::npos ? "" : authority.substr(colon + 1);
        if ((hostname != "127.0.0.1" && hostname != "localhost") ||
            authority.find('@') != std::string::npos ||
            (!port.empty() && port.find_first_not_of("0123456789") != std::string::npos))
            throw std::runtime_error("insecure signaling permitted only on loopback");
    }
    if (a.seconds < 5 || a.seconds > 3600) throw std::runtime_error("--seconds outside 5..3600");
    if (a.relayOnly && a.ice.empty()) throw std::runtime_error("--relay-only requires TURN server");
    return a;
}
struct Probe {
    Options opt;
    std::string token;
    int pc = -1, ws = -1, dc = -1;
    std::atomic<bool> stop{false}, connected{false};
    std::mutex mutex;
    bool remoteSdp = false;
    std::vector<std::pair<std::string, std::string>> queued;
    int pingId = 1;
    std::map<int, Clock::time_point> pings;

    Probe(Options o, std::string t) : opt(std::move(o)), token(std::move(t)) {}
    static Probe &get(void *p) { return *static_cast<Probe *>(p); }
    void sendSignal(std::string msg) {
        if (ws >= 0 && rtcIsOpen(ws) && rtcSendMessage(ws, msg.c_str(), -1) < 0)
            std::cerr << "signaling send failed\n";
    }
    static void onLocalSdp(int, const char *sdp, const char *type, void *p) {
        if (sdp && type) {
            get(p).sendSignal(std::string(type) + "|" + b64(sdp));
            std::cout << "local SDP: " << type << "\n";
        }
    }
    static void onCandidate(int, const char *c, const char *mid, void *p) {
        if (c) get(p).sendSignal("candidate|" + b64(c) + "|" + b64(mid ? mid : ""));
    }
    static void onState(int, rtcState state, void *p) {
        auto &a = get(p);
        std::cout << "PeerConnection state=" << static_cast<int>(state) << "\n";
        if (state == RTC_CONNECTED) {
            char local[1024] = {}, remote[1024] = {};
            if (rtcGetSelectedCandidatePair(a.pc, local, sizeof(local), remote, sizeof(remote)) >= 0)
                std::cout << "selected ICE pair local=" << local << " remote=" << remote << "\n";
        }
        if (state == RTC_FAILED) a.stop = true;
    }
    static void onOpen(int, void *p) {
        get(p).connected = true;
        std::cout << "PROBE_CONNECTED data channel opened\n";
    }
    static void onClose(int, void *p) {
        get(p).connected = false;
        std::cout << "data channel closed\n";
    }
    static void onMessage(int, const char *msg, int size, void *p) {
        auto &a = get(p);
        if (!msg) return;
        std::string data = size < 0 ? std::string(msg) : std::string(msg, static_cast<size_t>(size));
        if (a.opt.role == "phone" && data.rfind("ping|", 0) == 0) {
            std::string reply = "pong|" + data.substr(5);
            rtcSendMessage(a.dc, reply.c_str(), -1);
        } else if (a.opt.role == "host" && data.rfind("pong|", 0) == 0) {
            try {
                int n = std::stoi(data.substr(5));
                std::lock_guard<std::mutex> lock(a.mutex);
                auto it = a.pings.find(n);
                if (it != a.pings.end()) {
                    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - it->second).count();
                    std::cout << "PROBE_RTT_MS=" << ms << " seq=" << n << "\n";
                    a.pings.erase(it);
                }
            } catch (...) { std::cerr << "invalid pong\n"; }
        }
    }
    void attach(int id) {
        if (id < 0) { stop = true; return; }
        dc = id;
        rtcSetUserPointer(dc, this);
        rtcSetOpenCallback(dc, onOpen);
        rtcSetClosedCallback(dc, onClose);
        rtcSetMessageCallback(dc, onMessage);
    }
    static void onDataChannel(int, int id, void *p) { get(p).attach(id); }
    static void onWsOpen(int, void *p) {
        auto &a = get(p);
        a.sendSignal("join|" + a.opt.room + "|" + a.opt.role + "|" + a.token);
    }
    static void onWsError(int, const char *s, void *p) {
        std::cerr << "signaling error: " << (s ? s : "unknown") << "\n";
        get(p).stop = true;
    }
    static void onWsClosed(int, void *p) { get(p).stop = true; }
    static void onSignal(int, const char *msg, int size, void *p) {
        auto &a = get(p);
        if (!msg) return;
        std::string value = size < 0 ? std::string(msg) : std::string(msg, static_cast<size_t>(size));
        if (value == "joined") { std::cout << "signaling registered\n"; return; }
        if (value == "ready") {
            if (a.opt.role == "host") a.attach(rtcCreateDataChannel(a.pc, "deskfuse-connection-probe"));
            return;
        }
        if (value == "peer_left") { a.stop = true; return; }
        try {
            auto sep = value.find('|');
            if (sep == std::string::npos) return;
            auto type = value.substr(0, sep);
            auto payload = value.substr(sep + 1);
            if (type == "offer" || type == "answer") {
                auto sdp = from64(payload);
                if (rtcSetRemoteDescription(a.pc, sdp.c_str(), type.c_str()) < 0) {
                    std::cerr << "remote SDP failed\n"; a.stop = true; return;
                }
                std::vector<std::pair<std::string, std::string>> candidates;
                {
                    std::lock_guard<std::mutex> lock(a.mutex);
                    a.remoteSdp = true;
                    candidates.swap(a.queued);
                }
                for (const auto &c : candidates) rtcAddRemoteCandidate(a.pc, c.first.c_str(), c.second.c_str());
            } else if (type == "candidate") {
                auto second = payload.find('|');
                if (second == std::string::npos) return;
                std::pair<std::string, std::string> candidate{from64(payload.substr(0, second)), from64(payload.substr(second + 1))};
                std::lock_guard<std::mutex> lock(a.mutex);
                if (!a.remoteSdp) a.queued.push_back(std::move(candidate));
                else rtcAddRemoteCandidate(a.pc, candidate.first.c_str(), candidate.second.c_str());
            }
        } catch (const std::exception &e) { std::cerr << "bad signal: " << e.what() << "\n"; }
    }
    int run() {
        rtcPreload();
        std::vector<const char *> servers;
        for (const auto &s : opt.ice) servers.push_back(s.c_str());
        rtcConfiguration config{};
        config.iceServers = servers.empty() ? nullptr : servers.data();
        config.iceServersCount = static_cast<int>(servers.size());
        config.iceTransportPolicy = opt.relayOnly ? RTC_TRANSPORT_POLICY_RELAY : RTC_TRANSPORT_POLICY_ALL;
        pc = rtcCreatePeerConnection(&config);
        if (pc < 0) throw std::runtime_error("rtcCreatePeerConnection failed");
        rtcSetUserPointer(pc, this);
        rtcSetLocalDescriptionCallback(pc, onLocalSdp);
        rtcSetLocalCandidateCallback(pc, onCandidate);
        rtcSetStateChangeCallback(pc, onState);
        rtcSetDataChannelCallback(pc, onDataChannel);
        ws = rtcCreateWebSocket(opt.signal.c_str());
        if (ws < 0) throw std::runtime_error("rtcCreateWebSocket failed");
        rtcSetUserPointer(ws, this);
        rtcSetOpenCallback(ws, onWsOpen);
        rtcSetClosedCallback(ws, onWsClosed);
        rtcSetErrorCallback(ws, onWsError);
        rtcSetMessageCallback(ws, onSignal);
        bool everConnected = false;
        auto deadline = Clock::now() + std::chrono::seconds(opt.seconds);
        auto next = Clock::now();
        while (!stop && Clock::now() < deadline) {
            if (connected) everConnected = true;
            if (opt.role == "host" && connected && Clock::now() >= next) {
                int n;
                {
                    std::lock_guard<std::mutex> lock(mutex);
                    n = pingId++;
                    pings[n] = Clock::now();
                }
                std::string msg = "ping|" + std::to_string(n);
                rtcSendMessage(dc, msg.c_str(), -1);
                next = Clock::now() + std::chrono::seconds(1);
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
        }
        if (dc >= 0) { rtcDeleteDataChannel(dc); dc = -1; }
        if (pc >= 0) { rtcDeletePeerConnection(pc); pc = -1; }
        if (ws >= 0) { rtcDeleteWebSocket(ws); ws = -1; }
        rtcCleanup();
        std::cout << "PROBE_RESULT=" << (everConnected ? "CONNECTED" : "NOT_CONNECTED") << "\n";
        return everConnected ? 0 : 2;
    }
};
} // namespace

int main(int argc, char **argv) {
    try {
        auto options = parse(argc, argv);
        auto token = std::getenv("DESKFUSE_PROBE_TOKEN");
        if (!token || std::string(token).size() < 32)
            throw std::runtime_error("DESKFUSE_PROBE_TOKEN must contain 32+ random characters");
        Probe probe(std::move(options), token);
        return probe.run();
    } catch (const std::exception &e) {
        std::cerr << "Probe error: " << e.what() << "\n";
        return 1;
    }
}
