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
