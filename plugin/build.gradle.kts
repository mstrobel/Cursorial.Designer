plugins {
    alias(libs.plugins.kotlinJvm)
    alias(libs.plugins.intelliJPlatform)
}

group = "dev.cursorial"
version = providers.gradleProperty("pluginVersion").get()

repositories {
    mavenCentral()

    intellijPlatform {
        defaultRepositories()
        jetbrainsRuntime()
    }
}

dependencies {
    intellijPlatform {
        // useInstaller = false resolves the SDK from the JetBrains IntelliJ repository instead of
        // the installer CDN; it requires an explicit JetBrains Runtime dependency (below).
        // This mirrors AvaloniaRider's setup and also works for EAP/snapshot versions later.
        rider(libs.versions.riderSdk) {
            useInstaller = false
        }
        jetbrainsRuntime()
    }

    // JSON: the platform bundles Gson (com.google.gson), available on the compile classpath
    // through the intellijPlatform dependency above — no extra dependency needed.
}

kotlin {
    // The 2026.x platform runs on and requires Java 21 bytecode.
    jvmToolchain(21)
}

intellijPlatform {
    pluginConfiguration {
        ideaVersion {
            sinceBuild = "261"
            // untilBuild is left at its default (261.*). Widen deliberately when testing newer IDEs.
        }
    }

    // No settings pages with searchable options yet; skip the (slow) index build.
    buildSearchableOptions = false
}

tasks {
    runIde {
        jvmArgs("-Xmx2g")
    }
}
