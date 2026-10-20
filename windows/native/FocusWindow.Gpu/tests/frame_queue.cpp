#include "latest_frame_queue.h"

#include <array>
#include <future>
#include <iostream>
#include <memory>
#include <stdexcept>

namespace
{
using namespace std::chrono_literals;
struct ManualClock
{
    using rep = int64_t;
    using period = std::nano;
    using duration = std::chrono::nanoseconds;
    using time_point = std::chrono::time_point<ManualClock>;
    static constexpr bool is_steady = true;
    static inline time_point current{};
    static time_point now() noexcept { return current; }
};

using Releases = std::array<int, 256>;
struct Frame
{
    int id = 0;
    int64_t sourceTime = 0;
    std::shared_ptr<Releases> releases;
};
struct Release
{
    void operator()(Frame& frame) const noexcept
    {
        if (frame.releases) ++(*frame.releases)[frame.id];
        frame.releases.reset();
    }
};
struct SourceTime
{
    int64_t operator()(const Frame& frame) const noexcept { return frame.sourceTime; }
};
using Queue = LatestFrameQueue<Frame, Release, SourceTime, ManualClock>;

void Check(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}

void FinalUpdateSurvivesPacing()
{
    const auto releases = std::make_shared<Releases>();
    Queue queue;
    Frame output;
    ManualClock::current = ManualClock::time_point{} + 5ms;
    queue.Push({1, 1234, releases});
    const auto due = ManualClock::time_point{} + 17ms;
    Check(!queue.Wait(output, 0ms, due), "early update must remain pending below the fps cap");
    Check((*releases)[1] == 0, "pacing must not release the final update");
    ManualClock::current = due;
    Check(queue.Wait(output, 0ms, due), "final update must be delivered when due without another repaint");
    Check(output.id == 1 && output.sourceTime == 1234, "pacing must preserve source identity and timestamp");
    Release{}(output);
    Check(!queue.Wait(output, 0ms), "idle source must not generate padding frames");
    queue.Close();
    Check((*releases)[1] == 1, "delivered frame must have exactly one owner");
}

void SlowConsumerGetsNewestCapture()
{
    const auto releases = std::make_shared<Releases>();
    Queue queue;
    Frame output;
    ManualClock::current = ManualClock::time_point{};
    Check(queue.Push({1, 100, releases}) == Queue::PushResult::Accepted, "first capture should occupy the pending slot");
    for (int id = 2; id <= 200; ++id)
        Check(queue.Push({id, id * 100, releases}) == Queue::PushResult::Replaced, "capture backlog must stay bounded to one");
    for (int id = 1; id < 200; ++id)
        Check((*releases)[id] == 1, "superseded capture must release its surface immediately");
    Check((*releases)[200] == 0, "newest capture must remain available");
    Check(queue.Wait(output, 0ms) && output.id == 200, "slow consumer must receive the newest picture, not a stale FIFO");
    Release{}(output);
    Check(!queue.Wait(output, 0ms), "consumed burst must not leave hidden captures");
    Check((*releases)[200] == 1, "newest capture must release once after consumption");
}

void DuplicateSourceTimesDoNotProduceVideo()
{
    const auto releases = std::make_shared<Releases>();
    Queue queue;
    Frame output;
    ManualClock::current = ManualClock::time_point{};
    queue.Push({1, 500, releases});
    Check(!queue.Wait(output, 0ms, ManualClock::time_point::min(), 500), "repeated compositor timestamp must not be encoded");
    queue.Push({2, 499, releases});
    Check(!queue.Wait(output, 0ms, ManualClock::time_point::min(), 500), "older compositor timestamp must not be encoded");
    queue.Push({3, 501, releases});
    Check(queue.Wait(output, 0ms, ManualClock::time_point::min(), 500) && output.id == 3, "new source timestamp must survive duplicate filtering");
    Release{}(output);
    for (int id = 1; id <= 3; ++id) Check((*releases)[id] == 1, "source-time filtering must release each surface once");
}

void FastCompositorDoesNotReduceEncoderCadence()
{
    const auto releases = std::make_shared<Releases>();
    Queue queue;
    Frame output;
    const auto epoch = ManualClock::time_point{};
    const auto encoderPeriod = 16666667ns; // 60 fps cap.
    const auto compositorPeriod = 6944444ns; // 144 Hz producer.
    auto nextCapture = epoch;
    int captures = 0;
    int64_t previousSource = 0;
    // The consumer reaches each encode deadline independently of WGC arrival.
    // Arrival-gap filtering incorrectly undershoots to about 48 fps here.
    for (int encoded = 0; encoded < 60; ++encoded)
    {
        const auto due = epoch + encoderPeriod * encoded;
        while (nextCapture <= due)
        {
            ManualClock::current = nextCapture;
            ++captures;
            queue.Push({captures, captures, releases});
            nextCapture += compositorPeriod;
        }
        ManualClock::current = due;
        Check(queue.Wait(output, 0ms, due, previousSource), "144 Hz source must provide a picture at every 60 fps deadline");
        Check(output.id == captures, "paced encoder must sample the most recent capture");
        previousSource = output.sourceTime;
        Release{}(output);
    }
    Check(!queue.Wait(output, 0ms), "producer stop must not generate extra video");
    for (int id = 1; id <= captures; ++id) Check((*releases)[id] == 1, "cadence conversion must not leak capture surfaces");
}

void CloseReleasesAndWakesConsumer()
{
    const auto releases = std::make_shared<Releases>();
    using RealQueue = LatestFrameQueue<Frame, Release, SourceTime>;
    RealQueue queue;
    queue.Push({1, 100, releases});
    std::promise<void> starting;
    auto started = starting.get_future();
    auto consumer = std::async(std::launch::async, [&]
    {
        Frame output;
        starting.set_value();
        return queue.Wait(output, 5s, std::chrono::steady_clock::now() + 1h);
    });
    started.wait();
    queue.Close();
    Check(consumer.wait_for(1s) == std::future_status::ready, "shutdown must wake a paced consumer promptly");
    Check(!consumer.get(), "closed queue must not deliver pending video");
    Check(queue.Push({2, 200, releases}) == RealQueue::PushResult::Closed, "closed queue must refuse later callbacks");
    queue.Close();
    Check((*releases)[1] == 1 && (*releases)[2] == 1, "shutdown must release pending and late surfaces exactly once");
}
}

int TestLatestFrameQueue()
{
    try
    {
        FinalUpdateSurvivesPacing();
        SlowConsumerGetsNewestCapture();
        DuplicateSourceTimesDoNotProduceVideo();
        FastCompositorDoesNotReduceEncoderCadence();
        CloseReleasesAndWakesConsumer();
        std::cout << "GPU capture pacing and ownership contracts passed without hardware\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
