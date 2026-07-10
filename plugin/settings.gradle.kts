rootProject.name = "cursorial-designer"

plugins {
    // Auto-provisions the JDK 21 toolchain required to compile against the 2026.1 platform,
    // regardless of which JDK runs Gradle itself (JDK 17..26 all work with Gradle 9.6).
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}
