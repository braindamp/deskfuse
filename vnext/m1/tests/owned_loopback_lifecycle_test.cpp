// Actual ABI/queue/object lifecycle, synthetic producer; never opens a device.
#include "../src/source_control.cpp"
#include <iostream>

int main() {
    DfLoopbackOpen result{sizeof(result), DF_LOOPBACK_ABI};
    DfLoopbackOptions invalid{sizeof(invalid), DF_LOOPBACK_ABI};
    if (deskfuse_open_owned_loopback(&invalid, &result)!=-1) return 1;
    auto owner=std::make_unique<OwnedLoopback>();
    auto* value=owner.get();
    value->worker=std::thread([value] {
        Block* slot=nullptr; uint64_t token=0;
        if (!value->queue.try_reserve(slot, token)) std::terminate();
        slot->frame_count=240; slot->first_device_frame=480; slot->qpc_100ns=1234567;
        slot->original_packet_offset_frames=240; slot->flags=AUDCLNT_BUFFERFLAGS_SILENT;
        slot->pcm.fill(0x5a);
        if (!value->queue.publish(token)) std::terminate();
        value->flags.fetch_or(DF_LOOPBACK_READY, std::memory_order_release);
        SetEvent(value->ready.get());
        if (WaitForSingleObject(value->stop.get(), 5000)!=WAIT_OBJECT_0) std::terminate();
        value->complete();
    });
    if (WaitForSingleObject(value->ready.get(), 5000)!=WAIT_OBJECT_0) return 2;
    if (deskfuse_loopback_join(value,0)!=1 || deskfuse_loopback_destroy(value)!=-1) return 3;
    DfLoopbackRecord record{sizeof(record), DF_LOOPBACK_ABI};
    if (deskfuse_loopback_try_borrow(value,&record)!=1 || record.pcm_bytes!=960 ||
        record.first_device_frame!=480 || record.qpc_100ns!=1234567 ||
        record.original_packet_offset_frames!=240 || record.pcm[959]!=0x5a) return 4;
    const auto token=record.token;
    if (deskfuse_loopback_try_borrow(value,&record)!=-1 || deskfuse_loopback_consume(value,token+1)!=-1) return 5;
    if (deskfuse_loopback_request_stop(value) || deskfuse_loopback_join(value,5000)) return 6;
    DfLoopbackStatus status{sizeof(status), DF_LOOPBACK_ABI};
    if (deskfuse_loopback_status(value,&status) ||
        (status.flags & (DF_LOOPBACK_JOINED|DF_LOOPBACK_TERMINAL|DF_LOOPBACK_BORROWED))!=
        (DF_LOOPBACK_JOINED|DF_LOOPBACK_TERMINAL|DF_LOOPBACK_BORROWED) ||
        deskfuse_loopback_destroy(value)!=-1 || record.pcm[959]!=0x5a) return 7;
    if (deskfuse_loopback_consume(value,token) || deskfuse_loopback_consume(value,token)!=-1) return 8;
    value->flags.fetch_or(DF_LOOPBACK_STOP_UNCONFIRMED, std::memory_order_release);
    if (deskfuse_loopback_join(value,0)!=-1 || deskfuse_loopback_destroy(value)!=-1) return 9;
    // Test-only cleanup of the synthetic fault; production has no clear operation.
    value->flags.fetch_and(~DF_LOOPBACK_STOP_UNCONFIRMED, std::memory_order_release);
    owner.release();
    if (deskfuse_loopback_destroy(value)) return 10;
    std::cout << "PASS: async owner timeout, exact borrow token, joined borrow custody, uncertain Stop quarantine\n";
}
