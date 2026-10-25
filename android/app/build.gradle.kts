plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

android {
    namespace = "com.focuswindow.client"
    compileSdk = 35
    ndkVersion = "30.0.16248370"
    externalNativeBuild { cmake { path = file("src/main/cpp/CMakeLists.txt"); version = "3.31.6" } }

    defaultConfig {
        applicationId = "com.focuswindow.client"
        ndk { abiFilters += "arm64-v8a" }
        externalNativeBuild { cmake { arguments += "-DANDROID_STL=c++_shared" } }

        // API 30 gives us WindowMetrics, per-display refresh modes and a sane
        // insets model. Below that the density contract cannot be verified
        // reliably, and shipping it unverified would break the core promise.
        minSdk = 30
        targetSdk = 35

        versionCode = 3
        versionName = "0.3.0-dev"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        resourceConfigurations += listOf("en")
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            isMinifyEnabled = false
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
        freeCompilerArgs += listOf(
            "-opt-in=kotlin.RequiresOptIn",
            // Warnings are errors: a density product cannot afford drift.
            "-Werror",
        )
    }

    sourceSets["main"].jniLibs.srcDir("build/native-audio-jni")
    packaging {
        jniLibs.useLegacyPackaging = true
        resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}")
    }

    testOptions {
        unitTests.isIncludeAndroidResources = true
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.10.00")
    implementation(composeBom)
    androidTestImplementation(composeBom)

    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("androidx.lifecycle:lifecycle-service:2.8.7")

    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.material3:material3")
    debugImplementation("androidx.compose.ui:ui-tooling")
    implementation("androidx.compose.ui:ui-tooling-preview")

    // WindowMetricsCalculator + foldable posture (FR-MOB-02).
    implementation("androidx.window:window:1.3.0")

    // Pairing secrets at rest, hardware-backed where available (FR-TRUST-01).
    implementation("androidx.security:security-crypto:1.1.0-alpha06")

    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.9.0")
    testImplementation("org.robolectric:robolectric:4.14.1")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
}

/**
 * NFR-SEC-01 — the trust posture is a SHIP GATE, not a README claim.
 *
 * This task fails the build if the merged manifest ever acquires an
 * accessibility service, an ADB-adjacent permission or an identity permission.
 * It runs on release builds in CI, so the store-listing promise and the binary
 * cannot drift apart.
 */
val auditTrustPosture by tasks.registering {
    group = "verification"
    description = "Fails if the merged manifest violates the published trust posture."

    val manifestFile = layout.buildDirectory
        .file("intermediates/merged_manifest/release/AndroidManifest.xml")
    inputs.file(manifestFile).optional(true)

    doLast {
        val file = manifestFile.get().asFile
        if (!file.exists()) {
            logger.lifecycle("auditTrustPosture: no merged manifest yet, skipping")
            return@doLast
        }
        val xml = file.readText()
        val forbidden = listOf(
            "BIND_ACCESSIBILITY_SERVICE",
            "WRITE_SECURE_SETTINGS",
            "QUERY_ALL_PACKAGES",
            "READ_PHONE_STATE",
            "GET_ACCOUNTS",
            "android.accessibilityservice",
        )
        val hits = forbidden.filter { xml.contains(it) }
        if (hits.isNotEmpty()) {
            throw GradleException(
                "Trust posture violated — the manifest contains: $hits. " +
                    "The store listing promises no accessibility service, no ADB and no account.",
            )
        }
        logger.lifecycle("auditTrustPosture: clean (no accessibility, no ADB, no identity perms)")
    }
}

tasks.matching { it.name == "assembleRelease" }.configureEach {
    finalizedBy(auditTrustPosture)
}

// Build the native receiver used by the product APK, not just the lab binary.
val buildNativeAudioTransport by tasks.registering(Exec::class) {
    val crate = rootProject.file("../vnext/m2a/quiche-lab")
    val sdk = System.getenv("ANDROID_HOME") ?: System.getenv("ANDROID_SDK_ROOT")
    val ndk = System.getenv("ANDROID_NDK_HOME") ?: sdk?.let { "$it/ndk/30.0.16248370" }
    workingDir(crate)
    inputs.files(fileTree(crate.resolve("src")), crate.resolve("Cargo.toml"), crate.resolve("Cargo.lock"))
    outputs.file(layout.buildDirectory.file("native-audio-jni/arm64-v8a/libdeskfuse_quiche_lab.so"))
    if (ndk != null) environment("ANDROID_NDK_HOME", ndk)
    environment("CMAKE_TOOLCHAIN_FILE_aarch64_linux_android", crate.resolve("cmake/android-ndk-wrapper.cmake").absolutePath)
    environment("CMAKE_GENERATOR_aarch64_linux_android", "Ninja")
    environment("BINDGEN_EXTRA_CLANG_ARGS_aarch64-linux-android", "--target=aarch64-linux-android35")
    commandLine("cargo", "ndk", "-t", "arm64-v8a", "-P", "35", "-o",
        layout.buildDirectory.dir("native-audio-jni").get().asFile.absolutePath,
        "build", "--release", "--lib", "--locked")
}
tasks.named("preBuild").configure { dependsOn(buildNativeAudioTransport) }