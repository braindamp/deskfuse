#include <jni.h>
#include <dlfcn.h>
#include <atomic>
#include <cstdint>
#include <mutex>
namespace {
std::mutex load_lock;
void* runtime = nullptr;
using Run = int (*)(const char*);
using Stop = void (*)();
std::atomic<Stop> stop_function{nullptr};
bool load(JNIEnv* env, jstring path) {
    if (!runtime) {
        const char* name = env->GetStringUTFChars(path, nullptr);
        if (!name) return false;
        runtime = dlopen(name, RTLD_NOW | RTLD_LOCAL);
        env->ReleaseStringUTFChars(path, name);
    }
    if (!runtime) return false;
    stop_function.store(reinterpret_cast<Stop>(dlsym(runtime, "deskfuse_native_audio_stop")));
    return stop_function.load() != nullptr;
}
}
extern "C" JNIEXPORT jboolean JNICALL
Java_com_focuswindow_client_media_NativeAudioBridge_prepare(JNIEnv* env, jobject, jstring path) {
    std::lock_guard<std::mutex> lock(load_lock);
    if (!path || !load(env,path)) return false;
    auto prepare = reinterpret_cast<bool(*)()>(dlsym(runtime,"deskfuse_native_audio_prepare"));
    return prepare && prepare();
}
extern "C" JNIEXPORT jint JNICALL
Java_com_focuswindow_client_media_NativeAudioBridge_run(JNIEnv* env, jobject, jstring json, jstring path) {
    if (!json || !path) return -1;
    Run run = nullptr;
    {
        std::lock_guard<std::mutex> lock(load_lock);
        if (!runtime) {
            const char* name = env->GetStringUTFChars(path, nullptr);
            if (!name) return -1;
            runtime = dlopen(name, RTLD_NOW | RTLD_LOCAL);
            env->ReleaseStringUTFChars(path, name);
        }
        if (!runtime) return -1;
        run = reinterpret_cast<Run>(dlsym(runtime, "deskfuse_native_audio_run"));
        stop_function.store(reinterpret_cast<Stop>(dlsym(runtime, "deskfuse_native_audio_stop")));
    }
    if (!run || !stop_function.load()) return -1;
    const char* config = env->GetStringUTFChars(json, nullptr);
    if (!config) return -1;
    const auto result = run(config);
    env->ReleaseStringUTFChars(json, config);
    return result;
}
extern "C" JNIEXPORT void JNICALL
Java_com_focuswindow_client_media_NativeAudioBridge_stop(JNIEnv*, jobject) {
    if (auto stop = stop_function.load()) stop();
}

extern "C" JNIEXPORT jint JNICALL
Java_com_focuswindow_client_media_NativeAudioBridge_labVersion(JNIEnv* env,jobject,jstring path) {
    std::lock_guard<std::mutex> lock(load_lock);
    return path && load(env,path) && dlsym(runtime,"deskfuse_native_audio_observation_v1") ? 1 : 0;
}
extern "C" JNIEXPORT jstring JNICALL
Java_com_focuswindow_client_media_NativeAudioBridge_observe(JNIEnv* env,jobject,jstring owner,jstring path) {
    using Observe=int(*)(const char*,unsigned char*,unsigned);
    Observe observe=nullptr;
    { std::lock_guard<std::mutex> lock(load_lock);
      if(!owner || !path || !load(env,path))return nullptr;
      observe=reinterpret_cast<Observe>(dlsym(runtime,"deskfuse_native_audio_observation_v1")); }
    if(!observe)return nullptr;
    const char* id=env->GetStringUTFChars(owner,nullptr);if(!id)return nullptr;
    unsigned char bytes[8192]{};int size=observe(id,bytes,sizeof(bytes));
    env->ReleaseStringUTFChars(owner,id);
    return size>0 && size<static_cast<int>(sizeof(bytes)) ? env->NewStringUTF(reinterpret_cast<const char*>(bytes)) : nullptr;
}

namespace {
using PhoneOpen = void* (*)(const char*, uint64_t, uint32_t, uint64_t);
using PhoneReady = void (*)(void*);
using PhoneCall = int32_t (*)(const void*, uint8_t, const uint8_t*, size_t, uint64_t, uint8_t*, size_t,
    PhoneReady, void*);
using PhoneEnd = void (*)(void*);
PhoneOpen phone_open = nullptr;
std::atomic<PhoneCall> phone_call{nullptr};
std::atomic<PhoneEnd> phone_stop{nullptr};
std::atomic<PhoneEnd> phone_close{nullptr};
void phone_error(JNIEnv* env, const char* type) {
    if (auto cls = env->FindClass(type)) env->ThrowNew(cls, "Native first audio delivery failed");
}
struct ReadyCallback { JNIEnv* env; jobject task; jmethodID run; };
void notify_ready(void* context) {
    const auto& callback = *static_cast<const ReadyCallback*>(context);
    callback.env->CallVoidMethod(callback.task, callback.run);
}
}
extern "C" JNIEXPORT jlong JNICALL
Java_com_focuswindow_client_transport_AuthorityPhoneBridge_open(JNIEnv* env, jobject, jstring library,
    jstring address, jlong deadline, jint idle, jlong network) {
    PhoneOpen open;
    {
        std::lock_guard<std::mutex> lock(load_lock);
        if (!library || !address || deadline <= 0 || idle <= 0 || !network || !load(env, library)) return 0;
        phone_open = reinterpret_cast<PhoneOpen>(dlsym(runtime, "deskfuse_phone_audio_open_bound_v1"));
        phone_call.store(reinterpret_cast<PhoneCall>(dlsym(runtime, "deskfuse_phone_audio_call_v3")));
        phone_stop.store(reinterpret_cast<PhoneEnd>(dlsym(runtime, "deskfuse_phone_audio_stop")));
        phone_close.store(reinterpret_cast<PhoneEnd>(dlsym(runtime, "deskfuse_phone_audio_close")));
        open = phone_open;
    }
    if (!open || !phone_call.load() || !phone_stop.load() || !phone_close.load()) return 0;
    const auto* endpoint = env->GetStringUTFChars(address, nullptr);
    if (!endpoint) return 0;
    const auto handle = open(endpoint, uint64_t(deadline), uint32_t(idle), uint64_t(network));
    env->ReleaseStringUTFChars(address, endpoint);
    return reinterpret_cast<jlong>(handle);
}
extern "C" JNIEXPORT jbyteArray JNICALL
Java_com_focuswindow_client_transport_AuthorityPhoneBridge_call(JNIEnv* env, jobject, jlong handle,
    jint op, jbyteArray input, jlong deadline, jobject ready) {
    const auto call = phone_call.load();
    if (!handle || !input || !call || op < 1 || op > 135 || deadline < 0) {
        phone_error(env, "java/lang/IllegalArgumentException"); return nullptr;
    }
    jbyte in[8192], out[8192];
    const auto size = env->GetArrayLength(input);
    if (size > static_cast<jsize>(sizeof(in))) { phone_error(env, "java/lang/IllegalArgumentException"); return nullptr; }
    env->GetByteArrayRegion(input, 0, size, in);
    if (env->ExceptionCheck()) return nullptr;
    ReadyCallback callback{env, ready, nullptr};
    if (ready) {
        const auto cls = env->GetObjectClass(ready);
        if (!cls) return nullptr;
        callback.run = env->GetMethodID(cls, "run", "()V");
        env->DeleteLocalRef(cls);
        if (!callback.run) return nullptr;
    }
    const auto count = call(reinterpret_cast<void*>(handle), uint8_t(op), reinterpret_cast<const uint8_t*>(in),
        size, uint64_t(deadline), reinterpret_cast<uint8_t*>(out), sizeof(out),
        ready ? notify_ready : nullptr, &callback);
    if (env->ExceptionCheck()) return nullptr;
    if (count < 0) {
        phone_error(env, count == -2 ? "java/net/SocketTimeoutException" : "java/lang/IllegalStateException");
        return nullptr;
    }
    auto result = env->NewByteArray(count);
    if (result && count) env->SetByteArrayRegion(result, 0, count, out);
    return result;
}
extern "C" JNIEXPORT void JNICALL
Java_com_focuswindow_client_transport_AuthorityPhoneBridge_stop(JNIEnv*, jobject, jlong handle) {
    if (const auto stop = phone_stop.load(); stop && handle) stop(reinterpret_cast<void*>(handle));
}
extern "C" JNIEXPORT void JNICALL
Java_com_focuswindow_client_transport_AuthorityPhoneBridge_close(JNIEnv*, jobject, jlong handle) {
    if (const auto close = phone_close.load(); close && handle) close(reinterpret_cast<void*>(handle));
}
