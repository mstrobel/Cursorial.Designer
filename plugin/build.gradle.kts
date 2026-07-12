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

    publishing {
        // Marketplace Personal Access Token (plugins.jetbrains.com → My Tokens). Local publishes:
        // MARKETPLACE_TOKEN=... ./gradlew publishPlugin — CI injects it from a repo secret.
        token = providers.environmentVariable("MARKETPLACE_TOKEN")
    }

    pluginVerification {
        // ./gradlew verifyPlugin — runs the JetBrains Plugin Verifier against IDE builds matching
        // the since/until range. Point at a specific EAP with ides { ide("RD", "2026.2-EAP1") }
        // when pre-flighting the next release train.
        ides {
            recommended()
        }
    }
}

// The .NET preview host, published framework-dependent + portable: ONE output runs on any OS
// with a .NET 10 runtime (the plugin launches it as `dotnet <dll>`). Bundled under the plugin's
// dotnet/ directory in both the runIde sandbox and the buildPlugin zip, so a from-disk install
// works on machines without this repository.
val publishPreviewHost by tasks.registering(Exec::class) {
    group = "build"
    description = "dotnet-publishes Cursorial.Designer.PreviewHost for bundling into the plugin."
    workingDir = projectDir.parentFile
    val output = layout.buildDirectory.dir("previewHost")
    outputs.dir(output)
    commandLine(
        "dotnet", "publish", "Cursorial.Designer.PreviewHost",
        "-c", "Release",
        "-o", output.get().asFile.absolutePath,
    )
}

tasks {
    runIde {
        jvmArgs("-Xmx2g")
        // No mid-session hot swaps: buildPlugin refreshes the sandbox, and auto-reload silently
        // replaced the running plugin with partially re-registered extensions — features died
        // in ways that looked like real bugs. Updates apply on the next (re)launch only.
        jvmArgs("-Didea.auto.reload.plugins=false")
    }

    prepareSandbox {
        dependsOn(publishPreviewHost)
        from(layout.buildDirectory.dir("previewHost")) {
            into(pluginName.get() + "/dotnet")
        }

        // Make the sandbox feel like home: seed the developer's real Rider keymaps (once — the
        // sandbox stays authoritative for its own edits afterwards) and re-seed keymap-bearing
        // plugins EVERY run, because prepareSandbox rebuilds the plugins directory and would
        // otherwise wipe sandbox-installed plugins like XWinKeymap — which custom keymaps derive
        // from, so losing it silently reverts bindings. Quit the sandbox IDE gracefully (Cmd+Q,
        // not Ctrl+C on Gradle) or freshly-changed settings may never flush to disk.
        val seededPlugins = listOf("XWinKeymap")

        doLast {
            val riderConfig = File(System.getProperty("user.home"), "Library/Application Support/JetBrains")
                .listFiles { candidate -> candidate.isDirectory && candidate.name.startsWith("Rider") }
                ?.maxByOrNull { it.lastModified() }
                ?: return@doLast

            val sandboxConfig = sandboxConfigDirectory.get().asFile
            val sourceKeymaps = riderConfig.resolve("keymaps")
            val targetKeymaps = sandboxConfig.resolve("keymaps")
            if (sourceKeymaps.isDirectory && !targetKeymaps.exists())
                sourceKeymaps.copyRecursively(targetKeymaps)

            val sourceSelection = riderConfig.resolve("options/keymap.xml")
            val targetSelection = sandboxConfig.resolve("options/keymap.xml")
            if (sourceSelection.isFile && !targetSelection.exists()) {
                targetSelection.parentFile.mkdirs()
                sourceSelection.copyTo(targetSelection)
            }

            val sandboxPlugins = sandboxPluginsDirectory.get().asFile
            for (name in seededPlugins) {
                val source = riderConfig.resolve("plugins/$name")
                val target = sandboxPlugins.resolve(name)
                if (source.isDirectory && !target.exists())
                    source.copyRecursively(target)
            }
        }
    }
}
