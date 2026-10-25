import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// Default server URL baked into the APK: ./gradlew assembleRelease -PserverUrl=https://my-tunnel
val serverUrl: String = (project.findProperty("serverUrl") as String?)
    ?.trim()?.trimEnd('/')
    ?.takeIf { it.isNotEmpty() }
    ?: "http://10.0.2.2:5090"

// Release signing: read from keystore.properties (never committed) when present.
val keystorePropsFile = rootProject.file("keystore.properties")
val keystoreProps = Properties().apply {
    if (keystorePropsFile.exists()) keystorePropsFile.inputStream().use { load(it) }
}
val hasReleaseKey = keystorePropsFile.exists()

android {
    namespace = "mx.ipn.upiicsa.signaltrace"
    compileSdk = 37

    defaultConfig {
        applicationId = "mx.ipn.upiicsa.signaltrace"
        minSdk = 26
        targetSdk = 37
        versionCode = 1
        versionName = "1.0.0"
        buildConfigField("String", "DEFAULT_SERVER_URL", "\"$serverUrl\"")
    }

    signingConfigs {
        if (hasReleaseKey) {
            create("release") {
                storeFile = rootProject.file(keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (hasReleaseKey) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    testOptions {
        unitTests.all {
            // JVM tests read reference/test-vectors.json and, optionally, talk to a running server.
            it.systemProperty("st.vectors", rootProject.file("../reference/test-vectors.json").absolutePath)
            System.getenv("ST_TEST_SERVER")?.let { url -> it.environment("ST_TEST_SERVER", url) }
            System.getenv("ST_TEST_PASSWORD")?.let { pw -> it.environment("ST_TEST_PASSWORD", pw) }
        }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.core)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.okhttp)
    implementation(libs.zxing.embedded)

    testImplementation(libs.junit)
}
