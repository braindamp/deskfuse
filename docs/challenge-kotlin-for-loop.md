# Challenge: the Kotlin compiler rejects a plain C-style `for` loop

## The question

**Why does the Kotlin compiler in this project reject a legal C-style `for`
loop, and what is the correct fix?**

## The symptom

Compiling `:app:assembleDebug` fails with:

```
e: file:///D:/Coding/DeskFuse/v0.3/android/app/src/main/java/com/focuswindow/client/media/PcAudioOutput.kt:209:13
   Method 'iterator()' is ambiguous for this expression. Applicable candidates:
       fun <T> Enumeration<T>.iterator(): Iterator<T>
       fun <T> Iterator<T>.iterator(): Iterator<T>
       fun <K, V> Map<out K, V>.iterator(): Iterator<Map.Entry<K, V>>
       fun <K, V> MutableMap<K, V>.iterator(): MutableIterator<MutableMap.MutableEntry<K, V>>
       fun CharSequence.iterator(): CharIterator
       fun BufferedInputStream.iterator(): ByteIterator
e: ...PcAudioOutput.kt:209:25 Syntax error: Expecting 'in'.
e: ...PcAudioOutput.kt:209:26 Syntax error: Expecting ')'.
e: ...PcAudioOutput.kt:209:30 Unresolved reference 'ch'.
```

The reported line is a completely ordinary loop:

```kotlin
for (var ch = 0; ch < channels; ch++) {
    val at = (index * channels + ch) * 2
    pcm[at] = (sample.toInt() and 0xFF).toByte()
    pcm[at + 1] = ((sample.toInt() shr 8) and 0xFF).toByte()
}
```

The compiler is treating the loop as a Kotlin `for (x in y)` and complaining
that the *subject* has an ambiguous `iterator()`. In Kotlin, `for (init;
condition; update)` is valid syntax and has been since 1.0.

## The verified A/B

Same file, same compiler, same build, one construct changed:

| Version of the loop | Result |
| --- | --- |
| `for (var ch = 0; ch < channels; ch++) { ... }` | **fails** with the errors above |
| `repeat(channels) { ch -> ... }` | **compiles**, and the whole app assembles |

Reproduced twice, from a clean `app/build` (the Kotlin caches were deleted
between the attempts, so it is not a stale-cache artefact).

## The environment

* Gradle 8.9 (`gradle-8.9-bin.zip`)
* Android Gradle Plugin **8.7.2**
* Kotlin **2.0.21** (the K2 compiler), plugins: `kotlin.android`, `kotlin.jvm`,
  `kotlin.plugin.compose`, `kotlin.plugin.serialization`
* JDK 17.0.20.1 (Temurin), `jvmTarget = "17"`
* Windows 11 / 10 build 19045, project at `D:\Coding\DeskFuse\v0.3\android`
* The failing file is `app/src/main/java/com/focuswindow/client/media/PcAudioOutput.kt`,
  inside a private method of a class that also uses Compose-free Android APIs.

## What I want to know

1. **Is this a known K2 (Kotlin 2.0.21) parser bug?** If so, what is the
   issue number, is it fixed in a later Kotlin, and what is the smallest
   change that avoids it (the language version, the plugin, a rewrite of the
   loop)?
2. **What exactly triggers it?** The loop is inside a lambda
   (`repeat(frameCount) { index -> ... }`) and inside a class. Is a nested
   C-style `for` inside a lambda the trigger, or something else? A minimal
   reproducer would settle it.
3. **Is there a project-level fix** better than rewriting every C-style loop
   (for example a compiler flag, a language version, or a newer Kotlin
   plugin), given that AGP 8.7.2 and Gradle 8.9 are fixed by the rest of
   the build?
4. Is there a **forward-looking** recommendation for this project: keep Kotlin
   2.0.21 and avoid C-style loops, or move to a Kotlin version where the
   parser behaves?

## Notes

* This is **not** a platform, encoding or line-ending problem. The file is
  plain ASCII with CRLF endings, and the error is reported at the correct
  line and column.
* The same file compiles when the loop is `repeat` ✓, and the rest of the
  project (which uses `for` loops elsewhere, including C-style ones in other
  files) compiles unchanged ✓ — so the trigger is local to this construct.
* The workaround in place is `repeat(n) { i -> ... }`.

## How to reproduce

```
cd D:\Coding\DeskFuse\v0.3\android
set JAVA_HOME=D:\Coding\DeskFuse\toolchain\jdk-17.0.20.1+1
set ANDROID_HOME=D:\Android\sdk
gradlew.bat :app:compileDebugKotlin --no-daemon
```

and in `app/src/main/java/com/focuswindow/client/media/PcAudioOutput.kt`
around line 209, swap the `repeat(channels) { ch -> ... }` back to
`for (var ch = 0; ch < channels; ch++) { ... }`.
