plugins {
    id("com.android.application")
    // The Flutter Gradle Plugin must be applied after the Android and Kotlin Gradle plugins.
    id("dev.flutter.flutter-gradle-plugin")
}

android {
    namespace = "az.neoprint.neoprint"
    compileSdk = flutter.compileSdkVersion
    ndkVersion = flutter.ndkVersion

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    defaultConfig {
        // TODO: Specify your own unique Application ID (https://developer.android.com/studio/build/application-id.html).
        applicationId = "az.neoprint.neoprint"
        // You can update the following values to match your application needs.
        // For more information, see: https://flutter.dev/to/review-gradle-config.
        minSdk = flutter.minSdkVersion
        targetSdk = flutter.targetSdkVersion
        // Uses the version code from pubspec.yaml. When using split APKs, 1000 * ABI_VERSION
        // is added automatically by Flutter. (https://developer.android.com/studio/build/configure-apk-splits#configure-APK-versions)
        // You can force using the value of versionCode by specifying the `-P force-version-code-ignoring-abi=true`
        // flag during build.
        versionCode = flutter.versionCode
        versionName = flutter.versionName

        // Only the ABIs this build is for (T-292). `--target-platform` limits
        // Flutter's own libraries, but the plugins' native ones (MapLibre, the
        // ML Kit barcode engine) came for every ABI - an arm64 APK carried
        // x86_64 and 32-bit arm copies too. Not with --split-per-abi, which
        // makes its own per-ABI APKs and refuses a filter beside it. Flutter's
        // filter is off for this (gradle.properties, disable-abi-filtering).
        val abis = mapOf(
            "android-arm" to "armeabi-v7a",
            "android-arm64" to "arm64-v8a",
            "android-x64" to "x86_64",
        )
        val targets = (project.findProperty("target-platform") as String?)
            ?.split(",")
            ?.mapNotNull { abis[it.trim()] }
        // Without a target platform (a plain Gradle build), Flutter's own three.
        if (project.findProperty("split-per-abi") != "true") {
            ndk { abiFilters += targets?.takeIf { it.isNotEmpty() } ?: abis.values }
        }
    }

    buildTypes {
        release {
            // TODO: Add your own signing config for the release build.
            // Signing with the debug keys for now, so `flutter run --release` works.
            signingConfig = signingConfigs.getByName("debug")
        }
    }
}

kotlin {
    compilerOptions {
        jvmTarget = org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17
    }
}

flutter {
    source = "../.."
}

dependencies {
    // The document scanner (plan sections 2.0, 2.11): Google ML Kit through
    // Google Play services - free, from Google itself; docs/THIRD_PARTY.md.
    implementation("com.google.android.gms:play-services-mlkit-document-scanner:16.0.0")
}
