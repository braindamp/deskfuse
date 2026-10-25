// Exercise the actual writer lifetime and serialization without an endpoint.
#define DESKFUSE_CAPTURE_LIFECYCLE_TEST
#include "../src/wasapi_capture.cpp"

int main() {
    // Use queried legal periods, including an already-running short graph.
    // Missing/mismatched observations must fail before a render stream opens.
    {
        EnginePeriods p;
        p.legal_result = p.current_result = S_OK;
        p.current_format_matches = true;
        p.normal = p.maximum = p.current = 512;
        p.minimum = p.fundamental = 128;
        if (minimum_legal_period(p) != 128) return 10;
        p.current = 128;
        if (minimum_legal_period(p) != 128) return 11;
        const auto rejects = [](const EnginePeriods& input) {
            try { (void)minimum_legal_period(input); return false; }
            catch (const std::exception&) { return true; }
        };
        p.current_format_matches = false;
        if (!rejects(p)) return 12;
        p.current_format_matches = true;
        p.minimum = 129;
        if (!rejects(p)) return 13;
        p.minimum = 128; p.fundamental = 0;
        if (!rejects(p)) return 14;
        p.fundamental = 128; p.current_result = E_FAIL;
        if (!rejects(p)) return 15;
    }

    // A Stop/check exception must unwind through a joined writer, not a
    // joinable std::thread destructor (which would terminate the process).
    {
        CaptureWriter::Queue queue;
        bool caught = false;
        try {
            CaptureWriter writer(queue, INVALID_HANDLE_VALUE);
            check(E_FAIL, "injected WASAPI Stop");
        } catch (const std::exception&) { caught = true; }
        Block block;
        if (!caught || queue.try_push(block)) return 1;
    }

    // Keep the pipe reader OPEN but do not consume: the synchronous writer
    // must be cancelled after its final-drain allowance, rather than hang.
    {
        HANDLE read = nullptr, write = nullptr;
        if (!CreatePipe(&read, &write, nullptr, 1024)) return 2;
        Handle pipe_read(read), pipe_write(write);
        CaptureWriter::Queue queue;
        Block block;
        block.frame_count = kMaxFrames;
        for (size_t n = 0; n < kQueueSlots; ++n) {
            block.first_device_frame = n * kMaxFrames;
            if (!queue.try_push(block)) return 3;
        }
        CaptureWriter writer(queue, pipe_write.get());
        const auto start = std::chrono::steady_clock::now();
        writer.finish();
        if (!writer.cancelled()) return 4;
        bool rejected = false;
        try { writer.check_result(); }
        catch (const std::exception&) { rejected = true; }
        if (!rejected || std::chrono::steady_clock::now() - start >
                         std::chrono::seconds(3)) return 5;
    }

    // A healthy reader receives the complete DFWC v2 record, then the writer
    // drains and exits without cancellation or changing the local live ABI.
    {
        HANDLE read = nullptr, write = nullptr;
        if (!CreatePipe(&read, &write, nullptr, 4096)) return 6;
        Handle pipe_read(read), pipe_write(write);
        CaptureWriter::Queue queue;
        Block block;
        block.frame_count = kMaxFrames;
        block.first_device_frame = 12345;
        block.original_packet_offset_frames = 240;
        block.pcm[0] = 0xA5;
        if (!queue.try_push(block)) return 7;
        CaptureWriter writer(queue, pipe_write.get());
        writer.finish();
        writer.check_result();
        if (writer.cancelled()) return 8;
        std::array<uint8_t, kHeaderSize + kPayloadLimit> record{};
        DWORD received = 0;
        if (!ReadFile(pipe_read.get(), record.data(), DWORD(record.size()), &received, nullptr) ||
            received != record.size() || record[0] != 'D' || record[4] != 2 ||
            record[36] != 240 || record[kHeaderSize] != 0xA5) return 9;
    }
    std::cout << "PASS: writer exception cleanup, blocked-pipe cancellation, complete v2 record\n";
    return 0;
}
