// Linux/glibc-only test interposer. CTest LD_PRELOADs this DSO before
// libsamplerate. Each SRC process call is bracketed on the SAME thread.
// This measures actual libc allocation attempts originating from the loaded
// libsamplerate DSO, unlike wrapping only operator new in the C++ executable.
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cerrno>
extern "C" {
void* __libc_malloc(size_t);
void* __libc_calloc(size_t,size_t);
void* __libc_realloc(void*,size_t);
void  __libc_free(void*);
void* __libc_memalign(size_t,size_t);
}
namespace {
thread_local bool probe_enabled=false;
thread_local uint64_t allocation_attempts=0;
}
extern "C" __attribute__((visibility("default"))) void df_probe_start() noexcept {
    allocation_attempts=0;
    probe_enabled=true;
}
extern "C" __attribute__((visibility("default"))) uint64_t df_probe_finish() noexcept {
    probe_enabled=false;
    return allocation_attempts;
}
extern "C" void* malloc(size_t n) noexcept {
    if(probe_enabled)++allocation_attempts;
    return __libc_malloc(n);
}
extern "C" void* calloc(size_t n,size_t s) noexcept {
    if(probe_enabled)++allocation_attempts;
    return __libc_calloc(n,s);
}
extern "C" void* realloc(void* p,size_t n) noexcept {
    if(probe_enabled)++allocation_attempts;
    return __libc_realloc(p,n);
}
extern "C" void free(void* p) noexcept {
    __libc_free(p);
}
extern "C" void* aligned_alloc(size_t a,size_t n) noexcept {
    if(probe_enabled)++allocation_attempts;
    if(a==0 || (a&(a-1))!=0 || n%a!=0) {
        errno=EINVAL;
        return nullptr;
    }
    return __libc_memalign(a,n);
}
extern "C" int posix_memalign(void** p,size_t a,size_t n) noexcept {
    if(!p || a<sizeof(void*) || (a&(a-1))!=0)return EINVAL;
    if(probe_enabled)++allocation_attempts;
    void* out=__libc_memalign(a,n);
    if(!out)return ENOMEM;
    *p=out;return 0;
}
