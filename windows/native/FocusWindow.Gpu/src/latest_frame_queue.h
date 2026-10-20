#pragma once

#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <optional>
#include <utility>

// One pending source frame, replaced before encode. Pacing defers consumption,
// never discards the final update just because it arrived below the fps cap.
// Clock/Release/SourceTime are injected types so this production queue can be
// tested without WinRT, D3D, a real HWND or timer sleeps.
template<class Frame, class Release, class SourceTime, class Clock = std::chrono::steady_clock>
class LatestFrameQueue
{
public:
    using TimePoint = typename Clock::time_point;
    enum class PushResult { Accepted, Replaced, Closed };

    ~LatestFrameQueue() { Close(); }

    PushResult Push(Frame frame)
    {
        std::optional<Frame> obsolete;
        PushResult result;
        {
            std::lock_guard<std::mutex> lock(gate_);
            if (closed_)
            {
                obsolete.emplace(std::move(frame));
                result = PushResult::Closed;
            }
            else
            {
                if (latest_) obsolete.emplace(std::move(*latest_));
                result = latest_ ? PushResult::Replaced : PushResult::Accepted;
                latest_.emplace(std::move(frame));
            }
        }
        if (obsolete) Release{}(*obsolete);
        // Replacement does not change the deadline. Avoid waking a paced
        // consumer for every compositor arrival while it already has a frame.
        if (result == PushResult::Accepted) ready_.notify_one();
        return result;
    }

    bool Wait(Frame& output, std::chrono::milliseconds timeout,
        TimePoint notBefore = TimePoint::min(), int64_t afterSourceTime = 0)
    {
        const auto deadline = Clock::now() + timeout;
        std::unique_lock<std::mutex> lock(gate_);
        while (!closed_)
        {
            if (latest_)
            {
                if (afterSourceTime != 0 && SourceTime{}(*latest_) <= afterSourceTime)
                {
                    auto stale = std::move(*latest_);
                    latest_.reset();
                    lock.unlock();
                    Release{}(stale);
                    lock.lock();
                    if (Clock::now() >= deadline) return false;
                    continue;
                }
                if (Clock::now() >= notBefore)
                {
                    output = std::move(*latest_);
                    latest_.reset();
                    return true;
                }
                if (Clock::now() >= deadline) return false;
                ready_.wait_until(lock, notBefore < deadline ? notBefore : deadline);
            }
            else
            {
                if (Clock::now() >= deadline) return false;
                ready_.wait_until(lock, deadline, [this] { return closed_ || latest_.has_value(); });
            }
        }
        return false;
    }

    void Close()
    {
        std::optional<Frame> obsolete;
        {
            std::lock_guard<std::mutex> lock(gate_);
            closed_ = true;
            if (latest_) obsolete.emplace(std::move(*latest_));
            latest_.reset();
        }
        ready_.notify_all();
        if (obsolete) Release{}(*obsolete);
    }

private:
    std::mutex gate_;
    std::condition_variable ready_;
    std::optional<Frame> latest_;
    bool closed_ = false;
};
