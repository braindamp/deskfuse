import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    kotlin("jvm")
}

// Test-only JVM harness: compile the exact shipping Android crypto sources.
// Android API stubs live only here and never enter the APK.
java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}
kotlin {
    compilerOptions.jvmTarget.set(JvmTarget.JVM_17)
    sourceSets.main {
        kotlin.setSrcDirs(listOf(projectDir, rootProject.file("app/src/main/java")))
        kotlin.include(
            "ProtocolV3KotlinCheck.kt", "SecureRecordWriterCheck.kt", "jvm-stubs/**/*.kt",
            "com/focuswindow/client/transport/PairingPayload.kt",
            "com/focuswindow/client/transport/SecureChannel.kt",
            "com/focuswindow/client/transport/SecureRecordWriter.kt",
        )
    }
}

val checkVectors by tasks.registering(JavaExec::class) {
    classpath = sourceSets.main.get().runtimeClasspath
    mainClass.set("com.focuswindow.protocol3.ProtocolV3KotlinCheck")
    args("vectors", rootProject.file("../tests/protocol3/protocol-v3-vectors.properties"))
}
val checkRecordWriter by tasks.registering(JavaExec::class) {
    classpath = sourceSets.main.get().runtimeClasspath
    mainClass.set("com.focuswindow.client.transport.SecureRecordWriterCheck")
    args(rootProject.file("../tests/protocol3/protocol-v3-vectors.properties"))
}
tasks.named("check") { dependsOn(checkVectors, checkRecordWriter) }

// The C# test launches this runnable classpath to exercise a live TCP handshake.
tasks.register<Jar>("interopJar") {
    archiveFileName.set("deskfuse-protocol-v3-kotlin.jar")
    destinationDirectory.set(rootProject.layout.buildDirectory.dir("protocol3"))
    duplicatesStrategy = DuplicatesStrategy.EXCLUDE
    from(sourceSets.main.get().output)
    from({ configurations.runtimeClasspath.get().map { if (it.isDirectory) it else zipTree(it) } })
}
