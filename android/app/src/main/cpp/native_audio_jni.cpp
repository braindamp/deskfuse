#include <jni.h>
#include <dlfcn.h>
#include <atomic>
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
